using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Google.Tests.ConfiguredGeminiFixture;

namespace Asgard.Loki.LLM.Google.Tests;

/// <summary>原生候选、模型身份、错误与资源终态不能被自定义路径或认证绕过。</summary>
public sealed class ConfiguredGeminiBoundaryTests
{
    /// <summary>每种认证检查缺失、重复、结束后生成、身份漂移和迟到全局失败。</summary>
    public static IEnumerable<object[]> Faults()
    { foreach (var profile in Enumerable.Range(0, 5)) foreach (var fault in new[] { "missing", "duplicate", "post", "response", "model", "error", "blocked" }) yield return [profile, fault]; }
    /// <summary>任一已见候选未结束或发生迟到失败时，不可泄露已缓存成功尾帧。</summary>
    [Theory] [MemberData(nameof(Faults))]
    public async Task NativeFault_DiscardsBufferedEnding(int profile, string fault)
    {
        var source = Window.GetProperty("sse").GetString()!;
        if (fault == "missing") source = string.Concat(Window.GetProperty("events").EnumerateArray().Where(item => !item.TryGetProperty("candidates", out var candidates) || !candidates.EnumerateArray().Any(candidate => candidate.GetProperty("index").GetInt32() == 3 && candidate.TryGetProperty("finishReason", out _))).Select(Frame));
        else
        {
            var extra = fault switch { "duplicate" => "{\"candidates\":[{\"index\":0,\"finishReason\":\"STOP\"}]}", "post" => "{\"candidates\":[{\"index\":0,\"content\":{\"parts\":[{\"text\":\"late\"}]}}]}", "response" => "{\"responseId\":\"changed\",\"usageMetadata\":{}}", "model" => "{\"modelVersion\":\"changed\",\"usageMetadata\":{}}", _ => JsonSerializer.Serialize(Window.GetProperty(fault)) };
            source += "data: "+extra+"\n\n";
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new GoogleHttpFactory(handler); await using var sdk = Build(Provider(factory, profile), profile); List<GeminiContentEvent> seen = [];
        if (fault is "error" or "blocked") { var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, true, seen)); Assert.Equal(HttpStatusCode.OK, failure.StatusCode); Assert.Equal(JsonSerializer.Serialize(Window.GetProperty(fault)), failure.ResponseBody); Assert.Equal("text/event-stream", failure.ContentType); } else await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, true, seen)); Assert.DoesNotContain(seen, Ending); Assert.Equal(1, handler.Calls);
    }
    /// <summary>JSON 和 SSE 每种认证都验证真实 EOF 与最终客户端关闭取消。</summary>
    public static IEnumerable<object[]> LateCases()
    { foreach (var row in ConfiguredGeminiTests.Cases()) foreach (var cancel in new[] { false, true }) yield return [row[0], row[1], cancel]; }
    /// <summary>尾部读取错误与最后取消必须阻止完整结果或流终态交付。</summary>
    [Theory] [MemberData(nameof(LateCases))]
    public async Task LateFailure_PreventsDelivery(int profile, bool stream, bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new OllamaBodyStream(Bytes(stream ? Window.GetProperty("sse").GetString()! : Window.GetProperty("result").GetRawText())) { FailAtEof = !cancel }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, stream))); var factory = new GoogleHttpFactory(handler) { OnDispose = () => { if (cancel) cancellation.Cancel(); } }; await using var sdk = Build(Provider(factory, profile), profile); List<GeminiContentEvent> seen = [];
        var failure = await Record.ExceptionAsync(() => Invoke(sdk, stream, seen, token: cancellation.Token)); Assert.NotNull(failure); if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>实际 HTTP 错误媒体、状态和任意二进制字节保持原文。</summary>
    [Theory] [MemberData(nameof(ConfiguredGeminiTests.Cases), MemberType = typeof(ConfiguredGeminiTests))]
    public async Task OriginalHttpError_IsPreserved(int profile, bool stream)
    {
        byte[] bytes = [0, 255, 128, 42]; var body = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => { var response = Reply(body, status: HttpStatusCode.TooManyRequests); response.Content.Headers.ContentType = new("application/octet-stream"); return Task.FromResult(response); }); var factory = new GoogleHttpFactory(handler); await using var sdk = Build(Provider(factory, profile), profile); var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, stream, [])); Assert.Equal(bytes, failure.Body.ToArray()); Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode); Assert.Equal("application/octet-stream", failure.ContentType); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>消费者提前退出释放当前拥有资源，不读取剩余数据或后台重试。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task EarlyExit_ClosesOwnedCall(int profile)
    {
        var body = new OllamaBodyStream(Bytes(Window.GetProperty("sse").GetString()!)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new GoogleHttpFactory(handler); await using var sdk = Build(Provider(factory, profile), profile); await foreach (var item in sdk.LLM.Gemini.StreamAsync(Request(), new() { SessionId = "configured-gemini" }, TestContext.Current.CancellationToken)) { Assert.False(Ending(item)); break; } Assert.False(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>首帧原生全局错误或阻断是原始窗口，不合成候选或计数。</summary>
    [Theory] [InlineData("error")] [InlineData("blocked")]
    public async Task InitialNativeFailure_RemainsOriginal(string kind)
    {
        var original = Window.GetProperty(kind); var body = new OllamaBodyStream(Bytes(Frame(original))); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new GoogleHttpFactory(handler); await using var sdk = Build(Provider(factory, 2), 2); List<GeminiContentEvent> seen = []; await Invoke(sdk, true, seen, _ => { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); }); Assert.True(JsonElement.DeepEquals(original, Assert.Single(seen).Data)); Assert.False(seen[0].Data.TryGetProperty("candidates", out _)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>未知 type 不能屏蔽真实候选载体；同样字段只在业务元数据中出现时保持原文。</summary>
    [Fact]
    public async Task TypedKnownCandidate_RemainsSubjectToNativeTerminal()
    {
        var events = Window.GetProperty("events").EnumerateArray().Select(item => JsonNode.Parse(item.GetRawText())!.AsObject()).ToArray(); foreach (var item in events.Where(item => item.ContainsKey("candidates"))) item["type"] = "vendor.extension"; var source = string.Concat(events.Select(item => "data: "+item.ToJsonString()+"\n\n")); var body = new OllamaBodyStream(Bytes(source)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new GoogleHttpFactory(handler); await using var sdk = Build(Provider(factory)); List<GeminiContentEvent> seen = []; await Invoke(sdk, true, seen, item => { if (item.Data.TryGetProperty("candidates", out var candidates) && candidates.EnumerateArray().Any(candidate => candidate.TryGetProperty("finishReason", out _))) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } }); Assert.Equal(events.Length, seen.Count); for (var i = 0; i < seen.Count; i++) Assert.True(JsonNode.DeepEquals(events[i], JsonNode.Parse(seen[i].Data.GetRawText())));
    }
}
