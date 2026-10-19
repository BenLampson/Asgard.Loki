using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>真实原生适配器与显式转换装饰器必须保持同样的错误、关联和终态边界。</summary>
public sealed class TypedStreamErrorProviderTests
{
    /// <summary>未知事件和已知事件扩展分别验证默认严格策略与显式损耗许可。</summary>
    public static IEnumerable<object[]> Policies()
    {
        foreach (var protocol in new[] { LlmProtocol.ClaudeMessages, LlmProtocol.Responses })
        foreach (var known in new[] { false, true }) foreach (var allowLoss in new[] { false, true })
            yield return [protocol, known, allowLoss];
    }

    /// <summary>最终原生地址、认证、请求与完整目标快照都经过公共 SDK。</summary>
    [Theory] [MemberData(nameof(Policies))]
    public async Task NativePipeline_RetainsDiagnosticAndWaitsForEof(LlmProtocol protocol, bool known, bool allowLoss)
    {
        var source = Source(protocol, known); var body = new GroundingCitationReadStream(Wire(source));
        using var handler = Handler(protocol, body);
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ChatCompletions, protocol, options: new() { AllowLoss = allowLoss });
        List<ChatCompletionEvent> seen = [];
        if (allowLoss)
        {
            await Collect(client, seen, TestContext.Current.CancellationToken, body);
            var expected = TypedStreamErrorFixtures.Read("ChatGolden"); Assert.Equal(expected.Count, seen.Count);
            for (var i = 0; i < seen.Count; i++) Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected[i]), seen[i].Data));
            Assert.All(seen, item => Assert.NotNull(item.Metadata.Conversion));
            Assert.Contains(seen[^1].Metadata.Conversion!.Diagnostics, item => item.Path == "$event.error" && item.Code == "unsupported_field");
            Assert.DoesNotContain(seen[^1].Metadata.Conversion!.Diagnostics, item => item.Code == "upstream_stream_error");
            if (known) Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(source[4]), seen[1].Metadata.Conversion!.UpstreamData));
        }
        else
        {
            var failure = await Assert.ThrowsAsync<LlmConversionException>(() => Collect(client, seen, TestContext.Current.CancellationToken, body));
            Assert.Contains(failure.Diagnostics, item => item.Code == (known ? "unsupported_field" : "unknown_event"));
            Assert.DoesNotContain(failure.Diagnostics, item => item.Code == "upstream_stream_error");
            NoTerminal(seen);
        }
        Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>正常源终态之后的错误、传输失败和取消均不能先交付目标成功。</summary>
    [Theory]
    [InlineData(LlmProtocol.ClaudeMessages, "late-error")] [InlineData(LlmProtocol.Responses, "late-error")]
    [InlineData(LlmProtocol.ClaudeMessages, "read")] [InlineData(LlmProtocol.Responses, "read")]
    [InlineData(LlmProtocol.ClaudeMessages, "dispose")] [InlineData(LlmProtocol.Responses, "dispose")]
    [InlineData(LlmProtocol.ClaudeMessages, "cancel")] [InlineData(LlmProtocol.Responses, "cancel")]
    [InlineData(LlmProtocol.ClaudeMessages, "truncated")] [InlineData(LlmProtocol.Responses, "truncated")]
    public async Task NativeFailure_NeverPublishesConvertedTerminal(LlmProtocol protocol, string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var source = Source(protocol, false);
        if (kind == "truncated") source.RemoveAt(source.Count - 1);
        if (kind == "late-error") source.Add(new() { ["type"] = "error", ["error"] = new JsonObject { ["type"] = "overloaded_error" } });
        var body = new GroundingCitationReadStream(Wire(source))
        { FailAtEof = kind == "read", FailDispose = kind == "dispose", OnEof = kind == "cancel" ? cancellation.Cancel : null };
        using var handler = Handler(protocol, body);
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ChatCompletions, protocol, options: new() { AllowLoss = true });
        List<ChatCompletionEvent> seen = [];
        var failure = await Record.ExceptionAsync(() => Collect(client, seen, cancellation.Token, body));
        if (kind == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else if (kind is "read" or "dispose") Assert.IsType<IOException>(failure);
        else if (kind == "late-error")
        {
            var upstream = Assert.IsType<LlmUpstreamException>(failure);
            Assert.Equal(HttpStatusCode.OK, upstream.StatusCode); Assert.Equal(source[^1].ToJsonString(), upstream.ResponseBody);
        }
        else Assert.IsType<InvalidDataException>(failure);
        Assert.NotEmpty(seen); NoTerminal(seen); Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>已知扩展挂在实际发出工具增量的原生事件上，便于验证原文快照。</summary>
    private static List<JsonObject> Source(LlmProtocol protocol, bool known)
    {
        var source = TypedStreamErrorFixtures.Source(protocol, !known);
        if (known) source[4]["error"] = JsonNode.Parse("""{"type":"overloaded_error","message":"business metadata"}""");
        return source;
    }

    /// <summary>不注入 SSE 名称来改变 JSON 类型，也不以 DONE 修补原生终态。</summary>
    private static string Wire(IEnumerable<JsonObject> source) => string.Concat(source.Select(item => "data: " + item.ToJsonString() + "\n\n"));

    /// <summary>替代 HTTP 处理器覆盖真实最终请求，不读取网络或真实 Key。</summary>
    private static StubHttpHandler Handler(LlmProtocol protocol, Stream body) => new(async (request, token) =>
    {
        Assert.Equal("https://wire.invalid/v1/" + (protocol == LlmProtocol.ClaudeMessages ? "messages" : "responses"), request.RequestUri!.AbsoluteUri);
        if (protocol == LlmProtocol.ClaudeMessages) Assert.Equal("bound-key", Assert.Single(request.Headers.GetValues("x-api-key")));
        else Assert.Equal("bound-key", request.Headers.Authorization!.Parameter);
        var data = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
        Assert.Equal("model-a", data["model"]!.GetValue<string>()); Assert.True(data["stream"]!.GetValue<bool>());
        Assert.Contains("call_1", data.ToJsonString()); Assert.Contains("lookup", data.ToJsonString());
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        response.Content.Headers.ContentType = new("text/event-stream"); return response;
    });

    /// <summary>目标终态只能在源 EOF 和资源释放完成后出现。</summary>
    private static async Task Collect(ILokiClient client, List<ChatCompletionEvent> seen, CancellationToken token, GroundingCitationReadStream body)
    {
        var request = ConversionFixtures.Request(LlmProtocol.ChatCompletions).Deserialize<ChatCompletionRequest>()! with { Model = "public" };
        await foreach (var item in client.LLM.ChatCompletions.StreamAsync(request, new() { SessionId = "typed-error-session" }, token))
        {
            if (item.Data.GetProperty("choices")[0].GetProperty("finish_reason").ValueKind != JsonValueKind.Null)
            { Assert.True(body.Eof); Assert.True(body.Disposed); }
            seen.Add(item);
        }
    }

    /// <summary>已返回的工具增量不能冒充整次生成成功。</summary>
    private static void NoTerminal(IEnumerable<ChatCompletionEvent> seen)
        => Assert.All(seen, item => Assert.Equal(JsonValueKind.Null, item.Data.GetProperty("choices")[0].GetProperty("finish_reason").ValueKind));
}
