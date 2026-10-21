using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>公开 Responses 入口、真实 Gemini 原生供应商及显式转换装饰器的引用流验收。</summary>
public sealed class GroundingCitationStreamProviderTests
{
    /// <summary>实际请求、认证和完整目标事件匹配独立预期；引用仅在原生流 EOF/释放之后出现。</summary>
    [Theory] [InlineData(LlmCitationIndexUnit.UnicodeScalar)] [InlineData(LlmCitationIndexUnit.Utf16CodeUnit)] [InlineData(LlmCitationIndexUnit.Utf8Byte)]
    public async Task PublicSdk_MatchesFullNativeTargetAndRequest(LlmCitationIndexUnit unit)
    {
        var source = GroundingCitationStreamFixtures.Source(); var stream = ResponseStream(source);
        using var handler = Handler(stream, verifyRequest: true);
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, LlmProtocol.Gemini, options: GroundingCitationStreamFixtures.Options(unit));
        List<LlmJsonEvent> actual = [];
        await foreach (var item in client.LLM.Responses.StreamAsync(Request().Deserialize<ResponseRequest>()!,
            new() { SessionId = "citation-stream" }, TestContext.Current.CancellationToken))
        {
            if (item.Data.GetProperty("type").GetString() is "response.output_text.annotation.added" or "response.completed")
            { Assert.True(stream.Eof); Assert.True(stream.Disposed); }
            actual.Add(item);
        }
        var expected = GroundingCitationStreamFixtures.Expected(unit); Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++) GroundingCitationStreamFixtures.Equal(expected[i], actual[i].Data);
        Assert.All(actual, item => Assert.Empty(item.Metadata.Conversion!.Diagnostics));
        Assert.All(actual, item => { Assert.Equal(LlmProtocol.Gemini, item.Metadata.Conversion!.UpstreamProtocol); Assert.Equal(LlmProtocol.Responses, item.Metadata.Conversion.PublicProtocol); });
        GroundingCitationStreamFixtures.Equal(source[^1], actual[^1].Metadata.Conversion!.UpstreamData); Assert.Equal(1, handler.Calls);
    }
    /// <summary>无效关联、布局、源截断/迟到/阻断事件即使明确允许显示损耗也不会先公开引用或成功完成。</summary>
    [Theory] [InlineData("unicode")] [InlineData("part")] [InlineData("source")] [InlineData("text")]
    [InlineData("multiple")] [InlineData("truncated")] [InlineData("late")] [InlineData("blocked")]
    public async Task PublicSdk_InvalidSourceDoesNotLeakAnnotationsOrSuccess(string kind)
    {
        var source = GroundingCitationStreamFixtures.Source();
        switch (kind)
        {
            case "unicode": GroundingCitationStreamFixtures.Metadata(source, 1)["groundingSupports"]![0]!["segment"]!["startIndex"] = 1; break;
            case "part": GroundingCitationStreamFixtures.Metadata(source, 1)["groundingSupports"]![0]!["segment"]!["partIndex"] = 1; break;
            case "source": GroundingCitationStreamFixtures.Metadata(source, 2)["groundingSupports"]![0]!["groundingChunkIndices"]![0] = int.MaxValue; break;
            case "text": GroundingCitationStreamFixtures.Metadata(source, 2)["groundingSupports"]![0]!["segment"]!["text"] = "first occurrence"; break;
            case "multiple": source[0]["candidates"]![0]!["content"]!["parts"]!.AsArray().Add(new JsonObject { ["text"] = "other" }); break;
            case "truncated": source.RemoveAt(3); break;
            case "late": source.Add(source[0].DeepClone().AsObject()); break;
            case "blocked": source[^1]["promptFeedback"] = new JsonObject { ["blockReason"] = "SAFETY" }; break;
        }
        using var handler = Handler(ResponseStream(source));
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, LlmProtocol.Gemini,
            options: GroundingCitationStreamFixtures.Options() with { AllowLoss = true }); List<LlmJsonEvent> seen = [];
        if (kind == "multiple")
        {
            var error = await Assert.ThrowsAsync<LlmConversionException>(() => Collect(client, seen, TestContext.Current.CancellationToken));
            Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "grounding_stream_layout_not_representable");
        }
        else if (kind == "blocked")
        {
            var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => Collect(client, seen, TestContext.Current.CancellationToken));
            Assert.Equal(HttpStatusCode.OK, error.StatusCode); CustomToolConversionTests.Equal(source[^1], JsonNode.Parse(error.ResponseBody));
        }
        else await Assert.ThrowsAsync<InvalidDataException>(() => Collect(client, seen, TestContext.Current.CancellationToken));
        NoCitationOrTerminal(seen); Assert.Equal(1, handler.Calls);
    }
    /// <summary>候选 finish 及全部 grounding 已收到，但源读取/释放/取消失败仍不能交付目标完成或注解。</summary>
    [Theory] [InlineData("read")] [InlineData("dispose")] [InlineData("cancel")]
    public async Task PublicSdk_TransportTailFailureDoesNotPublishValidatedLookingCitations(string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stream = new GroundingCitationReadStream(Wire(GroundingCitationStreamFixtures.Source()))
        { FailAtEof = kind == "read", FailDispose = kind == "dispose", OnEof = kind == "cancel" ? cancellation.Cancel : null };
        using var handler = Handler(stream); await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, LlmProtocol.Gemini,
            options: GroundingCitationStreamFixtures.Options()); List<LlmJsonEvent> seen = [];
        if (kind == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(client, seen, cancellation.Token));
        else await Assert.ThrowsAsync<IOException>(() => Collect(client, seen, cancellation.Token));
        NoCitationOrTerminal(seen); Assert.True(stream.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>明确 MAX_TOKENS/SAFETY 仍映射 incomplete，已验证引用与部分文本不把它修补成 completed。</summary>
    [Theory] [InlineData("MAX_TOKENS", "max_output_tokens")] [InlineData("SAFETY", "content_filter")]
    public async Task PublicSdk_IncompleteRetainsCitationsAndNativeReason(string finish, string reason)
    {
        var source = GroundingCitationStreamFixtures.Source(); source[3]["candidates"]![0]!["finishReason"] = finish;
        using var handler = Handler(ResponseStream(source));
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, LlmProtocol.Gemini, options: GroundingCitationStreamFixtures.Options());
        var result = await ConvertingProviderTests.StreamAsync(client, LlmProtocol.Responses, Request());
        Assert.Equal("response.incomplete", result[^1].Data.GetProperty("type").GetString());
        Assert.Equal(reason, result[^1].Data.GetProperty("response").GetProperty("incomplete_details").GetProperty("reason").GetString());
        Assert.Equal("incomplete", result[^1].Data.GetProperty("response").GetProperty("output")[0].GetProperty("status").GetString());
        Assert.Equal(4, result[^1].Data.GetProperty("response").GetProperty("output")[0].GetProperty("content")[0].GetProperty("annotations").GetArrayLength());
        Assert.DoesNotContain(result, item => item.Data.GetProperty("type").GetString() == "response.completed"); Assert.Equal(1, handler.Calls);
    }
    /// <summary>缺少目标单位在 HTTP 前失败；显式配置不能靠请求模型名称猜测单位。</summary>
    [Fact]
    public async Task PublicSdk_MissingDeclarationDoesNotDispatch()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, LlmProtocol.Gemini,
            options: new() { GeminiGroundingStreamMode = GeminiGroundingStreamMode.SingleTextPart });
        await Assert.ThrowsAsync<ArgumentException>(() => ConvertingProviderTests.StreamAsync(client, LlmProtocol.Responses, Request())); Assert.Equal(0, handler.Calls);
    }
    /// <summary>源已结束后，调用者在第一个已验证引用处取消，也不能继续收到后续完成事件。</summary>
    [Fact]
    public async Task PublicSdk_CancellationBetweenFinalEventsSuppressesCompletion()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = Handler(ResponseStream(GroundingCitationStreamFixtures.Source()));
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, LlmProtocol.Gemini, options: GroundingCitationStreamFixtures.Options());
        List<LlmJsonEvent> seen = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in client.LLM.Responses.StreamAsync(Request().Deserialize<ResponseRequest>()!, new() { SessionId = "citation-stream" }, cancellation.Token))
            {
                seen.Add(item);
                if (item.Data.GetProperty("type").GetString() == "response.output_text.annotation.added") cancellation.Cancel();
            }
        });
        Assert.Single(seen, item => item.Data.GetProperty("type").GetString() == "response.output_text.annotation.added");
        Assert.DoesNotContain(seen, item => item.Data.GetProperty("type").GetString() is "response.completed" or "response.incomplete"); Assert.Equal(1, handler.Calls);
    }
    /// <summary>只校验协议生成，不发出真实 Google Search 请求或执行任何工具。</summary>
    private static StubHttpHandler Handler(Stream stream, bool verifyRequest = false) => new(async (request, token) =>
    {
        if (verifyRequest)
        {
            Assert.Equal("https://wire.invalid/v1/models/model-a:streamGenerateContent?alt=sse", request.RequestUri!.AbsoluteUri);
            Assert.Equal("bound-key", Assert.Single(request.Headers.GetValues("x-goog-api-key"))); Assert.DoesNotContain("bound-key", request.RequestUri.AbsoluteUri);
            CustomToolConversionTests.Equal(GroundingCitationStreamFixtures.Read("GeminiRequest"), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)));
        }
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }; response.Content.Headers.ContentType = new("text/event-stream"); return response;
    });
    /// <summary>转换回归独立构造原生 SSE，未复用生产编码器。</summary>
    private static string Wire(IEnumerable<JsonObject> source) => string.Concat(source.Select(item => "data: " + item.ToJsonString() + "\n\n"));
    /// <summary>每次响应使用独立源，不跨 SDK 客户端缓存资源。</summary>
    private static GroundingCitationReadStream ResponseStream(IEnumerable<JsonObject> source) => new(Wire(source));
    /// <summary>公开 Responses 用户请求。</summary>
    private static JsonObject Request() => CustomToolFixtures.Parse("""{"model":"public","input":"start"}""");
    /// <summary>异常时保留已公开事件，避免只检查最终 Task 失败而漏掉提前成功。</summary>
    private static async Task Collect(ILokiClient client, List<LlmJsonEvent> seen, CancellationToken token)
    {
        await foreach (var item in client.LLM.Responses.StreamAsync(Request().Deserialize<ResponseRequest>()!, new() { SessionId = "citation-stream" }, token)) seen.Add(item);
    }
    /// <summary>任何失败路径都不应先公开完成快照或尚待验证的引用。</summary>
    private static void NoCitationOrTerminal(IEnumerable<LlmJsonEvent> seen)
        => Assert.All(seen, item => Assert.DoesNotContain(item.Data.GetProperty("type").GetString()!,
            new[] { "response.completed", "response.incomplete", "response.output_text.annotation.added", "response.content_part.done", "response.output_item.done" }));
}
