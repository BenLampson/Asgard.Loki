using System.Net;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;
using static Asgard.Loki.LLM.OpenAI.Tests.ConfiguredHttpFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>显式地址和认证不改变原生错误、实际终态或拥有资源的提交边界。</summary>
public sealed class ConfiguredHttpBoundaryTests
{
    /// <summary>所有生成协议的认证分别检查缺失、重复和终态后已知生成。</summary>
    public static IEnumerable<object[]> Controls()
    { foreach (var mode in new[] { "chat", "responses", "text" }) foreach (var profile in Enumerable.Range(0, 4)) foreach (var fault in new[] { "missing", "duplicate", "post" }) yield return [mode, profile, fault]; }
    /// <summary>直到真实终态、EOF、资源关闭成功前，不可交付缓存的结束。</summary>
    [Theory] [MemberData(nameof(Controls))]
    public async Task InvalidNativeControl_DiscardsEnding(string mode, int profile, string fault)
    {
        var source = Part(mode).GetProperty("sse").GetString()!;
        if (fault == "missing") source = mode == "responses" ? string.Concat(Part(mode).GetProperty("events").EnumerateArray().Where(e => !e.TryGetProperty("type", out var type) || type.GetString() != "response.completed").Select(ConfiguredHttpFixture.Frame)) : source.Replace("data: [DONE]\n\n", "");
        else source += OpenAiHttpFixture.Frame(mode == "responses" ? fault == "duplicate" ? JsonSerializer.Serialize(Part(mode).GetProperty("events").EnumerateArray().Single(e => e.TryGetProperty("type", out var type) && type.GetString() == "response.completed")) : "{\"type\":\"response.output_text.delta\",\"delta\":\"late\"}" : fault == "duplicate" ? "[DONE]" : "{\"choices\":[{\"index\":0,\"text\":\"late\"}]}");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, mode, profile); List<LlmJsonEvent> seen = []; await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, mode, true, seen)); Assert.DoesNotContain(seen, Ending); Assert.Equal(1, handler.Calls);
    }
    /// <summary>JSON/SSE 所有原生操作分别检查尾部读取失败和最终客户端关闭取消。</summary>
    public static IEnumerable<object[]> LateCases()
    { foreach (var row in ConfiguredHttpTests.Cases()) foreach (var cancel in new[] { false, true }) yield return [row[0], row[1], row[2], cancel]; }
    /// <summary>没有完整提交边界，就不返回完整 JSON 或流结束。</summary>
    [Theory] [MemberData(nameof(LateCases))]
    public async Task LateFailure_PreventsDelivery(string mode, int profile, bool stream, bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new OllamaBodyStream(Bytes(stream ? Part(mode).GetProperty("sse").GetString()! : Part(mode).GetProperty("result").GetRawText())) { FailAtEof = !cancel }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, stream))); var factory = new OpenAiHttpFactory(handler) { OnDispose = () => { if (cancel) cancellation.Cancel(); } }; await using var sdk = Build(factory, mode, profile); List<LlmJsonEvent> seen = [];
        var failure = await Record.ExceptionAsync(() => Invoke(sdk, mode, stream, seen, cancellationToken: cancellation.Token)); Assert.NotNull(failure); if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>显式配置保留任意 HTTP 错误原始字节和实际媒体，不通过字符串替换非法编码。</summary>
    [Theory] [MemberData(nameof(ConfiguredHttpTests.Cases), MemberType = typeof(ConfiguredHttpTests))]
    public async Task RawHttpError_RemainsOriginal(string mode, int profile, bool stream)
    {
        byte[] bytes = [0, 255, 128, 42]; var body = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => { var response = Reply(body, false, status: HttpStatusCode.TooManyRequests); response.Content.Headers.ContentType = new("application/octet-stream"); return Task.FromResult(response); }); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, mode, profile);
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, mode, stream, [])); Assert.Equal(bytes, failure.Body.ToArray()); Assert.Equal("application/octet-stream", failure.ContentType); Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
