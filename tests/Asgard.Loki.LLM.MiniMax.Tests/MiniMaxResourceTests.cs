using System.Text;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.MiniMax.Tests.MiniMaxFixture;

namespace Asgard.Loki.LLM.MiniMax.Tests;

/// <summary>真实 EOF、正文/客户端关闭、最终取消和实际线协议不得被原生结束帧绕过。</summary>
public sealed class MiniMaxResourceTests
{
    /// <summary>同步 Dispose 成功不能替代原生流的失败异步关闭。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AsyncCloseFailure_DiscardsSuccess(bool stream)
    {
        var body = new MiniMaxAsyncFaultStream(Bytes(stream ? Golden.GetProperty("sse").GetString()! : Golden.GetProperty("result").GetRawText())); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, stream))); var factory = new MiniMaxHttpFactory(handler); var provider = new MiniMaxLlmProvider(factory); List<ChatCompletionEvent> seen = [];
        await Assert.ThrowsAsync<IOException>(async () => { if (stream) await Drain(provider, TestContext.Current.CancellationToken, seen); else await provider.CompleteAsync(Payload(), Context(provider), TestContext.Current.CancellationToken); }); Assert.True(body.ClosedAsync); Assert.DoesNotContain(seen, Ending); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>完整与流式调用使用同一资源/最终取消语义。</summary>
    public static IEnumerable<object[]> ClosureCases()
    { foreach (var stream in new[] { false, true }) foreach (var fault in new[] { "read", "body", "client", "cancel-eof", "cancel-body", "cancel-client" }) yield return [stream, fault]; }
    /// <summary>晚期读取、同步释放和取消失败均不能交付成功尾部。</summary>
    [Theory] [MemberData(nameof(ClosureCases))]
    public async Task LateFailure_DiscardsSuccess(bool stream, string fault)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new OllamaBodyStream(Bytes(stream ? Golden.GetProperty("sse").GetString()! : Golden.GetProperty("result").GetRawText())) { FailAtEof = fault == "read", FailDispose = fault == "body", OnEof = () => { if (fault == "cancel-eof") cancel.Cancel(); }, OnDispose = () => { if (fault == "cancel-body") cancel.Cancel(); } };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, stream))); var factory = new MiniMaxHttpFactory(handler) { OnDispose = () => { if (fault == "client") throw new IOException("late client close"); if (fault == "cancel-client") cancel.Cancel(); } }; var provider = new MiniMaxLlmProvider(factory); List<ChatCompletionEvent> seen = [];
        var failure = await Record.ExceptionAsync(async () => { if (stream) await Drain(provider, cancel.Token, seen); else await provider.CompleteAsync(Payload(), Context(provider), cancel.Token); }); Assert.NotNull(failure); if (fault.StartsWith("cancel", StringComparison.Ordinal)) Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>枚举者提前离开只释放当前调用，不读取到 EOF 或重试。</summary>
    [Fact]
    public async Task EarlyExit_ReleasesOwnedResources()
    {
        var body = new OllamaBodyStream(Bytes(Golden.GetProperty("sse").GetString()!)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new MiniMaxHttpFactory(handler); var provider = new MiniMaxLlmProvider(factory);
        await foreach (var item in provider.StreamAsync(Payload(true), Context(provider), TestContext.Current.CancellationToken)) { Assert.False(Ending(item)); break; } Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>成功 JSON/SSE 都需严格的字节、媒体和声明长度。</summary>
    public static IEnumerable<object[]> ResponseCases()
    { foreach (var stream in new[] { false, true }) foreach (var fault in new[] { "short", "long", "invalid-length", "budget", "utf8", "duplicates", "charset", "media", "native-status" }) yield return [stream, fault]; }
    /// <summary>不能以编码替换、截断、假 EOF 或无效状态码通过协议检查。</summary>
    [Theory] [MemberData(nameof(ResponseCases))]
    public async Task InvalidResponse_DiscardsSuccess(bool stream, string fault)
    {
        var bytes = Bytes(stream ? Golden.GetProperty("sse").GetString()! : Golden.GetProperty("result").GetRawText());
        if (fault == "utf8") bytes = [0xff]; if (fault == "duplicates") bytes = Bytes(stream ? "data: {\"x\":1,\"x\":2}\n\n" : "{\"x\":1,\"x\":2}"); if (fault == "native-status") bytes = Bytes(stream ? "data: {\"base_resp\":{\"status_code\":\"0\"}}\n\n" : "{\"base_resp\":{\"status_code\":\"0\"}}"); var body = new OllamaBodyStream(bytes);
        using var handler = new StubHttpHandler((_, _) => { var response = Reply(body, stream, fault == "short" ? bytes.Length-1 : fault == "long" ? bytes.Length+1 : null); if (fault == "invalid-length") { response.Content.Headers.Remove("Content-Length"); response.Content.Headers.TryAddWithoutValidation("Content-Length", "invalid"); } if (fault == "charset") response.Content.Headers.ContentType!.CharSet = "utf-16"; if (fault == "media") response.Content.Headers.ContentType = new("text/html"); return Task.FromResult(response); }); var factory = new MiniMaxHttpFactory(handler); var provider = new MiniMaxLlmProvider(factory, fault == "budget" ? new() { MaximumResponseBytes = 1, MaximumStreamBytes = 1 } : null); List<ChatCompletionEvent> seen = [];
        var failure = await Record.ExceptionAsync(async () => { if (stream) await Drain(provider, TestContext.Current.CancellationToken, seen); else await provider.CompleteAsync(Payload(), Context(provider), TestContext.Current.CancellationToken); }); Assert.NotNull(failure); Assert.True(failure is InvalidDataException or JsonException or DecoderFallbackException, failure.ToString()); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>发送和关闭后的真实请求均保持选中的完整目标与唯一认证。</summary>
    public static IEnumerable<object[]> WireCases()
    { foreach (var stream in new[] { false, true }) foreach (var field in new[] { "method", "root", "auth", "host", "content", "response", "late-root" }) yield return [stream, field]; }
    /// <summary>处理器或释放回调改写请求时必须失败，不复用另一个目标的成功结果。</summary>
    [Theory] [MemberData(nameof(WireCases))]
    public async Task ActualWireDrift_DiscardsSuccess(bool stream, string field)
    {
        HttpRequestMessage? sent = null; var body = new OllamaBodyStream(Bytes(stream ? Golden.GetProperty("sse").GetString()! : Golden.GetProperty("result").GetRawText())) { OnDispose = () => { if (field == "late-root") sent!.RequestUri = new("https://other.invalid/"); } };
        using var handler = new StubHttpHandler((request, _) => { sent = request; var response = Reply(body, stream); switch (field) { case "method": request.Method = HttpMethod.Get; break; case "root": request.RequestUri = new("https://other.invalid/"); break; case "auth": request.Headers.Authorization = new("Bearer", "other"); break; case "host": request.Headers.Host = "other.invalid"; break; case "content": request.Content = new StringContent("{}"); break; case "response": response.RequestMessage = new(HttpMethod.Post, request.RequestUri); break; } return Task.FromResult(response); }); var factory = new MiniMaxHttpFactory(handler); var provider = new MiniMaxLlmProvider(factory); List<ChatCompletionEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(async () => { if (stream) await Drain(provider, TestContext.Current.CancellationToken, seen); else await provider.CompleteAsync(Payload(), Context(provider), TestContext.Current.CancellationToken); }); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
