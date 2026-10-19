using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>公共 Gemini 入口经原生 OpenAI HTTP/SSE 的反向引用流与晚期失败验收。</summary>
public sealed class ResponseCitationStreamProviderTests
{
    /// <summary>最终 HTTP 正文、认证、全部返回事件和原始终态分别匹配独立样本。</summary>
    [Theory] [InlineData(LlmCitationIndexUnit.UnicodeScalar)] [InlineData(LlmCitationIndexUnit.Utf16CodeUnit)] [InlineData(LlmCitationIndexUnit.Utf8Byte)]
    public async Task PublicSdk_MatchesIndependentWireAndEvents(LlmCitationIndexUnit unit)
    {
        var source = ResponseCitationStreamFixtures.Source(unit); var body = new GroundingCitationReadStream(Wire(source)); using var handler = Handler(body, true); await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.Responses, options: ResponseCitationStreamFixtures.Options(unit)); List<LlmJsonEvent> seen = [];
        await foreach (var item in client.LLM.Gemini.StreamAsync(Request(), new() { SessionId = "reverse-citations" }, TestContext.Current.CancellationToken))
        {
            var candidate = item.Data.GetProperty("candidates")[0]; if (candidate.TryGetProperty("groundingMetadata", out _) || candidate.TryGetProperty("finishReason", out _)) { Assert.True(body.Eof); Assert.True(body.Disposed); } seen.Add(item);
        }
        var expected = ResponseCitationStreamFixtures.Read("Gemini").AsArray(); Assert.Equal(expected.Count, seen.Count); for (var i = 0; i < seen.Count; i++) GroundingCitationStreamFixtures.Equal(expected[i]!, seen[i].Data); Assert.All(seen, item => { Assert.Empty(item.Metadata.Conversion!.Diagnostics); Assert.Equal(LlmProtocol.Responses, item.Metadata.Conversion.UpstreamProtocol); Assert.Equal(LlmProtocol.Gemini, item.Metadata.Conversion.PublicProtocol); }); GroundingCitationStreamFixtures.Equal(source[^1], seen[^1].Metadata.Conversion!.UpstreamData); Assert.Equal(1, handler.Calls);
    }
    /// <summary>即使收到完整上游 completed，迟读/释放/取消仍不能公开目标引用或成功终态。</summary>
    [Theory] [InlineData("read")] [InlineData("dispose")] [InlineData("cancel")]
    public async Task LateTransportFailure_WithholdsGroundingAndFinish(string kind)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new GroundingCitationReadStream(Wire(ResponseCitationStreamFixtures.Source())) { FailAtEof = kind == "read", FailDispose = kind == "dispose", OnEof = kind == "cancel" ? cancel.Cancel : null }; using var handler = Handler(body); await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.Responses, options: ResponseCitationStreamFixtures.Options()); List<LlmJsonEvent> seen = [];
        if (kind == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(client, seen, cancel.Token)); else await Assert.ThrowsAsync<IOException>(() => Collect(client, seen, cancel.Token)); NoTerminal(seen); Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>字段关联、最终快照或后续上游错误不能被原生 HTTP 成功掩盖。</summary>
    [Theory] [InlineData("identity")] [InlineData("snapshot")] [InlineData("truncated")] [InlineData("error")]
    public async Task InvalidNativeSource_HasNoSuccessfulTarget(string kind)
    {
        var source = ResponseCitationStreamFixtures.Source(); if (kind == "identity") source[7]["item_id"] = "foreign"; if (kind == "snapshot") source[^1]["response"]!["output"]![0]!["content"]![0]!["annotations"]![0]!["title"] = "changed"; if (kind == "truncated") source.RemoveAt(source.Count - 1); if (kind == "error") source.Add(new() { ["type"] = "error", ["code"] = "fixture_error", ["message"] = "native failure" }); using var handler = Handler(new GroundingCitationReadStream(Wire(source))); await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.Responses, options: ResponseCitationStreamFixtures.Options() with { AllowLoss = true }); List<LlmJsonEvent> seen = []; var error = await Record.ExceptionAsync(() => Collect(client, seen, TestContext.Current.CancellationToken)); Assert.NotNull(error); if (kind == "error") { var native = Assert.IsType<LlmUpstreamException>(error); Assert.Equal(HttpStatusCode.OK, native.StatusCode); Assert.Contains("fixture_error", native.ResponseBody); } else Assert.True(error is InvalidDataException or LlmConversionException); NoTerminal(seen); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生 incomplete 映射 MAX_TOKENS/SAFETY，验证过的引用不把部分输出升级成 STOP。</summary>
    [Theory] [InlineData("max_output_tokens", "MAX_TOKENS")] [InlineData("content_filter", "SAFETY")]
    public async Task IncompleteResponse_RemainsIncomplete(string reason, string finish)
    {
        var source = ResponseCitationStreamFixtures.Source(); source[^1]["type"] = "response.incomplete"; source[^1]["response"]!["status"] = "incomplete"; source[^1]["response"]!["incomplete_details"] = new JsonObject { ["reason"] = reason }; using var handler = Handler(new GroundingCitationReadStream(Wire(source))); await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.Responses, options: ResponseCitationStreamFixtures.Options()); List<LlmJsonEvent> seen = []; await Collect(client, seen, TestContext.Current.CancellationToken); var candidate = seen[^1].Data.GetProperty("candidates")[0]; Assert.Equal(finish, candidate.GetProperty("finishReason").GetString()); Assert.True(candidate.TryGetProperty("groundingMetadata", out _)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>提前退出或首增量后取消释放借用 HTTP 响应，不触发完整流消费或生成完成。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task EarlyExit_DoesNotComplete(bool cancelAfterFirst)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new GroundingCitationReadStream(Wire(ResponseCitationStreamFixtures.Source())); using var handler = Handler(body); await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.Responses, options: ResponseCitationStreamFixtures.Options()); var count = 0;
        async Task Consume() { await foreach (var _ in client.LLM.Gemini.StreamAsync(Request(), new() { SessionId = "exit" }, cancel.Token)) { count++; if (cancelAfterFirst) cancel.Cancel(); else break; } }
        if (cancelAfterFirst) await Assert.ThrowsAnyAsync<OperationCanceledException>(Consume); else await Consume(); Assert.Equal(1, count); Assert.False(body.Eof); Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>缺少源单位声明在第一次 HTTP 之前拒绝，不能调用后再猜测字符单位。</summary>
    [Fact]
    public async Task MissingUnit_FailsBeforeHttp()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.Responses, options: new() { GeminiGroundingStreamMode = GeminiGroundingStreamMode.SingleTextPart }); await Assert.ThrowsAsync<ArgumentException>(() => Collect(client, [], TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>明确的原生 SSE/JSON 由替代处理器产生，不访问上游。</summary>
    private static StubHttpHandler Handler(Stream body, bool verify = false) => new(async (request, token) =>
    {
        if (verify)
        {
            Assert.Equal("https://wire.invalid/v1/responses", request.RequestUri!.AbsoluteUri); Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal("bound-key", request.Headers.Authorization.Parameter); var expected = JsonNode.Parse("""{"model":"model-a","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"sources"}]}],"temperature":0,"stream":true}"""); Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))));
        }
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new("text/event-stream"); return response;
    });
    /// <summary>流编码器只用于手工原生事件，不调用转换器产生响应。</summary>
    private static string Wire(IEnumerable<JsonObject> source) => string.Concat(source.Select(item => "data: " + item.ToJsonString() + "\n\n"));
    /// <summary>原生 Gemini 请求的数值零设置必须保留到实际上网正文。</summary>
    private static GeminiContentRequest Request() => JsonSerializer.Deserialize<GeminiContentRequest>("""{"model":"public","contents":[{"role":"user","parts":[{"text":"sources"}]}],"generationConfig":{"temperature":0}}""")!;
    /// <summary>保留失败前交付的所有事件，避免仅检查 Task 抛错而遗漏提前成功。</summary>
    private static async Task Collect(ILokiClient client, List<LlmJsonEvent> seen, CancellationToken token)
    { await foreach (var item in client.LLM.Gemini.StreamAsync(Request(), new() { SessionId = "reverse-citations" }, token)) seen.Add(item); }
    /// <summary>失败路径的所有已公开事件都不能含引用或 finishReason。</summary>
    private static void NoTerminal(IEnumerable<LlmJsonEvent> seen) => Assert.All(seen, item => { var candidate = item.Data.GetProperty("candidates")[0]; Assert.False(candidate.TryGetProperty("groundingMetadata", out _)); Assert.False(candidate.TryGetProperty("finishReason", out _)); });
}
