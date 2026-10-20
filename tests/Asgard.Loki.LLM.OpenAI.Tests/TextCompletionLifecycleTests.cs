using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.TextCompletionFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>真实文本 HTTP 分支的实际目标、资源、媒体和末尾故障不能先放出结果或结束帧。</summary>
public sealed class TextCompletionLifecycleTests
{
    /// <summary>完整 JSON 与 SSE 均独立注入读、释放及最终取消故障。</summary>
    public static IEnumerable<object[]> Faults()
    { foreach (var stream in new[] { false, true }) foreach (var fault in new[] { "read", "body", "client", "eof-cancel", "body-cancel", "client-cancel" }) yield return [stream, fault]; }
    /// <summary>任何关闭边界的失败都传播，暂存 choice 结束不能逃逸。</summary>
    [Theory] [MemberData(nameof(Faults))]
    public async Task LateResourceFault_WithholdsResult(bool stream, string fault)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var source = stream ? Text("events.sse") : Part("result").GetRawText();
        var body = new OllamaBodyStream(Bytes(source)) { FailAtEof = fault == "read", FailDispose = fault == "body", OnEof = fault == "eof-cancel" ? cancel.Cancel : null, OnDispose = fault == "body-cancel" ? cancel.Cancel : null };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, stream))); var factory = new OpenAiHttpFactory(handler) { OnDispose = () => { if (fault == "client") throw new IOException("client dispose"); if (fault == "client-cancel") cancel.Cancel(); } }; List<TextCompletionEvent> seen = [];
        var failure = await Record.ExceptionAsync(async () => { if (stream) await TextCompletionFixture.Stream(new(factory), cancel.Token, observe: seen.Add); else await Complete(new(factory), cancel.Token); });
        if (fault.EndsWith("cancel", StringComparison.Ordinal)) Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>成功的同步 Dispose 不可掩盖真实 DisposeAsync 故障。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AsyncDisposeFault_CannotReleaseSuccess(bool stream)
    {
        var body = new ModerationReadStream(Bytes(stream ? Text("events.sse") : Part("result").GetRawText())) { FailDispose = true }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, stream))); var factory = new OpenAiHttpFactory(handler); List<TextCompletionEvent> seen = [];
        var failure = await Record.ExceptionAsync(async () => { if (stream) await TextCompletionFixture.Stream(new(factory), TestContext.Current.CancellationToken, observe: seen.Add); else await Complete(new(factory), TestContext.Current.CancellationToken); }); Assert.IsType<IOException>(failure); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>每次实际发送和全部关闭后的原始请求都保持完整目标、认证及拥有内容。</summary>
    public static IEnumerable<object[]> WireFaults()
    { foreach (var stream in new[] { false, true }) foreach (var fault in new[] { "method", "root", "auth", "host", "content", "response-root", "late-root" }) yield return [stream, fault]; }
    /// <summary>处理器或晚期关闭回调换方法/地址/Key/Host/内容时明确失败。</summary>
    [Theory] [MemberData(nameof(WireFaults))]
    public async Task ChangedActualWire_CannotReleaseResult(bool stream, string fault)
    {
        HttpRequestMessage? sent = null; var body = new OllamaBodyStream(Bytes(stream ? Text("events.sse") : Part("result").GetRawText())) { OnDispose = () => { if (fault == "late-root") sent!.RequestUri = new("https://other.invalid/completions"); } };
        using var handler = new StubHttpHandler((message, _) =>
        {
            sent = message; var response = Reply(body, stream);
            switch (fault) { case "method": message.Method = HttpMethod.Put; break; case "root": message.RequestUri = new("https://other.invalid/completions"); break; case "auth": message.Headers.Authorization = new("Bearer", "other"); break; case "host": message.Headers.Host = "other.invalid"; break; case "content": message.Content = new StringContent("{}"); break; case "response-root": response.RequestMessage = new(HttpMethod.Post, "https://other.invalid/completions"); break; }
            return Task.FromResult(response);
        }); var factory = new OpenAiHttpFactory(handler); List<TextCompletionEvent> seen = [];
        var failure = await Record.ExceptionAsync(async () => { if (stream) await TextCompletionFixture.Stream(new(factory), TestContext.Current.CancellationToken, observe: seen.Add); else await Complete(new(factory), TestContext.Current.CancellationToken); }); Assert.IsType<InvalidDataException>(failure); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原始字节、完整长度和成功媒体类型必须通过同一 JSON/SSE 分支的严格读取。</summary>
    public static IEnumerable<object[]> ResponseFaults()
    { foreach (var stream in new[] { false, true }) foreach (var fault in new[] { "short", "long", "length-invalid", "body-limit", "utf8", "duplicates", "charset", "media" }) yield return [stream, fault]; }
    /// <summary>错误不能通过文本替换、尾部截断或非 JSON/SSE 的成功媒体被忽略。</summary>
    [Theory] [MemberData(nameof(ResponseFaults))]
    public async Task InvalidResponse_CannotReleaseSuccess(bool stream, string fault)
    {
        var bytes = Bytes(stream ? Text("events.sse") : Part("result").GetRawText()); if (fault == "utf8") bytes = [0xff]; if (fault == "duplicates") bytes = Bytes(stream ? Frame("{\"x\":1,\"x\":2}") : "{\"x\":1,\"x\":2}"); var body = new OllamaBodyStream(bytes);
        using var handler = new StubHttpHandler((_, _) =>
        {
            var response = Reply(body, stream, fault == "short" ? bytes.Length - 1 : fault == "long" ? bytes.Length + 1 : null);
            if (fault == "length-invalid") { response.Content.Headers.Remove("Content-Length"); response.Content.Headers.TryAddWithoutValidation("Content-Length", "invalid"); }
            if (fault == "charset") response.Content.Headers.ContentType!.CharSet = "utf-16";
            if (fault == "media") { response.Content.Headers.Remove("Content-Type"); response.Content.Headers.TryAddWithoutValidation("Content-Type", "not a valid media type"); } return Task.FromResult(response);
        }); var factory = new OpenAiHttpFactory(handler); var provider = new OpenAiLlmProvider(factory, new OpenAiHttpOptions { MaximumBodyBytes = fault == "body-limit" ? bytes.Length - 1 : 32 * 1024 * 1024 }); List<TextCompletionEvent> seen = [];
        var failure = await Record.ExceptionAsync(async () => { if (stream) await TextCompletionFixture.Stream(provider, TestContext.Current.CancellationToken, observe: seen.Add); else await Complete(provider, TestContext.Current.CancellationToken); }); Assert.NotNull(failure); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
