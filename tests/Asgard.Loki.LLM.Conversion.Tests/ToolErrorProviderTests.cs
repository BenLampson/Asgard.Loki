using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>明确工具错误格式通过公共 SDK 的最终 HTTP 与 SSE 流水线，不执行工具或模拟真实模型质量。</summary>
public sealed class ToolErrorProviderTests
{
    public static TheoryData<LlmProtocol, LlmToolErrorFormat, bool> Calls
    {
        get
        {
            TheoryData<LlmProtocol, LlmToolErrorFormat, bool> cases = [];
            foreach (var source in new[] { LlmProtocol.ClaudeMessages, LlmProtocol.Gemini })
                foreach (var format in new[] { LlmToolErrorFormat.GeminiResponseObject, LlmToolErrorFormat.GeminiErrorText })
                    foreach (var stream in new[] { false, true }) cases.Add(source, format, stream);
            return cases;
        }
    }
    /// <summary>完整原生请求金样与响应元数据分别比较，含实际模型、协议方法和原始结果关联。</summary>
    [Theory] [MemberData(nameof(Calls))]
    public async Task PublicSdk_PreservesCompleteNativeErrorHistory(LlmProtocol source, LlmToolErrorFormat format, bool stream)
    {
        var target = ToolErrorFixtures.Target(source);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var expected = ToolErrorFixtures.Read(target, format);
            if (target == LlmProtocol.Gemini) expected.Remove("model"); else expected["stream"] = stream;
            CustomToolConversionTests.Equal(expected, JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))); return ToolErrorFixtures.Reply(target, stream);
        });
        await using var client = ConvertingProviderTests.Build(handler, source, target, options: ToolErrorFixtures.Options(format));
        var request = ToolErrorFixtures.Read(source, format); request["model"] = "public"; var before = request.ToJsonString();
        if (stream)
        {
            var items = await ConvertingProviderTests.StreamAsync(client, source, request); Assert.NotEmpty(items); Assert.Contains(items, item => IsTerminal(item, source));
            Assert.All(items, item => Assert.Empty(item.Metadata.Conversion!.Diagnostics));
        }
        else
        {
            var result = await ConvertingProviderTests.CompleteAsync(client, source, request); Assert.Empty(result.Metadata.Conversion!.Diagnostics);
            CustomToolConversionTests.Equal(HostedSearchFixtures.TextResponse(target), JsonNode.Parse(result.Metadata.Conversion.UpstreamData.GetRawText()));
        }
        Assert.Equal(before, request.ToJsonString()); Assert.Equal(1, handler.Calls);
    }
    /// <summary>默认没有格式不能先发起 HTTP，完整/流式均拒绝语义降级。</summary>
    [Theory] [InlineData(LlmProtocol.Gemini, false)] [InlineData(LlmProtocol.Gemini, true)] [InlineData(LlmProtocol.ClaudeMessages, false)] [InlineData(LlmProtocol.ClaudeMessages, true)]
    public async Task MissingFormat_FailsBeforeHttp(LlmProtocol source, bool stream)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected HTTP"));
        await using var client = ConvertingProviderTests.Build(handler, source, ToolErrorFixtures.Target(source), options: new() { AllowLoss = true });
        var request = ToolErrorFixtures.Read(source, LlmToolErrorFormat.GeminiResponseObject); request["model"] = "public";
        await Assert.ThrowsAsync<LlmConversionException>(async () =>
        { if (stream) await ConvertingProviderTests.StreamAsync(client, source, request); else await ConvertingProviderTests.CompleteAsync(client, source, request); });
        Assert.Equal(0, handler.Calls);
    }
    /// <summary>请求历史的工具错误格式不会把上游生成流的晚到错误伪装成完成。</summary>
    [Theory] [InlineData(LlmProtocol.ClaudeMessages, LlmToolErrorFormat.GeminiResponseObject)] [InlineData(LlmProtocol.ClaudeMessages, LlmToolErrorFormat.GeminiErrorText)]
    [InlineData(LlmProtocol.Gemini, LlmToolErrorFormat.GeminiResponseObject)] [InlineData(LlmProtocol.Gemini, LlmToolErrorFormat.GeminiErrorText)]
    public async Task LateGenerationError_DoesNotBecomeSuccessfulToolResult(LlmProtocol source, LlmToolErrorFormat format)
    {
        var target = ToolErrorFixtures.Target(source); var events = HostedSearchFixtures.TextStream(target).Select(item => "data: " + item.ToJsonString() + "\n\n");
        var error = target == LlmProtocol.Gemini ? "{\"error\":{\"code\":500,\"message\":\"late\"}}" : "{\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":\"late\"}}";
        using var handler = new StubHttpHandler((_, _) =>
        {
            var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(string.Concat(events) + "data: " + error + "\n\n")) };
            reply.Content.Headers.ContentType = new("text/event-stream"); return Task.FromResult(reply);
        });
        await using var client = ConvertingProviderTests.Build(handler, source, target, options: ToolErrorFixtures.Options(format));
        var request = ToolErrorFixtures.Read(source, format); request["model"] = "public"; var received = new List<LlmJsonEvent>();
        var failure = await Record.ExceptionAsync(async () => { await foreach (var item in Stream(client, source, request)) received.Add(item); });
        if (target == LlmProtocol.Gemini) Assert.Equal("upstream_stream_error", Assert.Single(Assert.IsType<LlmConversionException>(failure).Diagnostics).Code);
        else Assert.IsType<InvalidDataException>(failure);
        Assert.NotEmpty(received); Assert.DoesNotContain(received, item => IsTerminal(item, source)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>只观察目标协议明确成功终态，不把正文中的 done 等普通字符串误判为完成。</summary>
    private static bool IsTerminal(LlmJsonEvent item, LlmProtocol protocol) => protocol == LlmProtocol.ClaudeMessages ? item.Type == "message_stop"
        : item.Data.TryGetProperty("candidates", out var candidates) && candidates.EnumerateArray().Any(candidate => candidate.TryGetProperty("finishReason", out _));
    /// <summary>通过实际公共入口消费流，以保留晚到错误前已经观察到的事件。</summary>
    private static IAsyncEnumerable<LlmJsonEvent> Stream(ILokiClient client, LlmProtocol protocol, JsonObject request)
        => protocol == LlmProtocol.ClaudeMessages
            ? client.LLM.ClaudeMessages.StreamAsync(request.Deserialize<ClaudeMessageRequest>()!, new() { SessionId = "wire-session" }, TestContext.Current.CancellationToken)
            : client.LLM.Gemini.StreamAsync(request.Deserialize<GeminiContentRequest>()!, new() { SessionId = "wire-session" }, TestContext.Current.CancellationToken);
}
