using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Ollama.Tests;

/// <summary>严格编码、有界记录、完整响应与模式媒体边界。</summary>
public sealed class TransportTests
{
    /// <summary>精确 UTF-8 字节限制包含 CR/空白，不截断、不按字符计数，HTTP 错误也有界。</summary>
    [Theory] [InlineData("stream", 0)] [InlineData("stream", -1)] [InlineData("json", 0)] [InlineData("json", -1)] [InlineData("error", 0)] [InlineData("error", -1)]
    public async Task ByteLimits_AreExact(string mode, int adjustment)
    {
        var text = OllamaFixture.Terminal[..^1] + ",\"future\":\"你好🌏\"}"; var bytes = Encoding.UTF8.GetBytes(text); var body = new OllamaBodyStream(bytes);
        using var handler = Handler(body, mode == "stream" ? "application/x-ndjson" : "application/json", mode == "error" ? 503 : 200);
        var provider = new OllamaLlmProvider(new StubHttpClientFactory(handler), options: new() { MaximumStreamRecordBytes = bytes.Length + adjustment, MaximumResponseBytes = bytes.Length + adjustment });
        async Task Invoke()
        { if (mode == "stream") { await foreach (var _ in provider.StreamAsync(OllamaFixture.Payload(), OllamaFixture.Context(), TestContext.Current.CancellationToken)) { } } else await provider.CompleteAsync(OllamaFixture.Payload(), OllamaFixture.Context(), TestContext.Current.CancellationToken); }
        if (adjustment < 0) await Assert.ThrowsAsync<InvalidDataException>(Invoke);
        else if (mode == "error") await Assert.ThrowsAsync<LlmUpstreamException>(Invoke); else await Invoke();
        Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>空行、CRLF 和无尾换行都允许；未知记录保持顺序，不能代替终态。</summary>
    [Theory] [InlineData("\n", true)] [InlineData("\n", false)] [InlineData("\r\n", true)] [InlineData("\r\n", false)]
    public async Task NdjsonFraming_PreservesIndependentRecords(string newline, bool finalNewline)
    {
        var text = " \t" + newline + OllamaFixture.Partial + newline + newline + "{\"future\":null}" + newline + OllamaFixture.Terminal + (finalNewline ? newline : "");
        var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(text)); using var handler = Handler(body, "application/x-ndjson"); var provider = new OllamaLlmProvider(new StubHttpClientFactory(handler));
        var items = new List<OllamaChatEvent>(); await foreach (var item in provider.StreamAsync(OllamaFixture.Payload(), OllamaFixture.Context(), TestContext.Current.CancellationToken)) items.Add(item);
        Assert.Equal(3, items.Count); Assert.True(OllamaFixture.Same(OllamaFixture.Partial, items[0].Data)); Assert.True(OllamaFixture.Same("{\"future\":null}", items[1].Data)); Assert.True(items[2].IsTerminal);
    }
    /// <summary>完整响应不能接受假 done、额外记录或错误媒体；流式不能接受 SSE 外壳。</summary>
    [Theory] [InlineData(false, "done")] [InlineData(false, "joined")] [InlineData(false, "media")] [InlineData(true, "media")] [InlineData(true, "json-partial")]
    public async Task WrongCompleteShapeOrMedia_IsRejected(bool stream, string kind)
    {
        var text = kind switch { "done" or "json-partial" => OllamaFixture.Terminal.Replace("true", "false"), "joined" => OllamaFixture.Terminal + "\n" + OllamaFixture.Terminal, _ => OllamaFixture.Terminal };
        var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(text)); using var handler = Handler(body, kind == "media" ? "text/event-stream" : "application/json");
        var provider = new OllamaLlmProvider(new StubHttpClientFactory(handler)); var error = await Record.ExceptionAsync(async () =>
        { if (stream) { await foreach (var _ in provider.StreamAsync(OllamaFixture.Payload(), OllamaFixture.Context(), TestContext.Current.CancellationToken)) { } } else await provider.CompleteAsync(OllamaFixture.Payload(), OllamaFixture.Context(), TestContext.Current.CancellationToken); });
        Assert.True(error is InvalidDataException or JsonException, error?.ToString()); Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>调用前已取消时不申请 HTTP 客户端。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PreCancellation_DoesNotDispatch(bool stream)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); cancellation.Cancel();
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected HTTP")); var factory = new OllamaHttpFactory(handler); var provider = new OllamaLlmProvider(factory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { if (stream) { await foreach (var _ in provider.StreamAsync(OllamaFixture.Payload(), OllamaFixture.Context(), cancellation.Token)) { } } else await provider.CompleteAsync(OllamaFixture.Payload(), OllamaFixture.Context(), cancellation.Token); });
        Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>输入无效 UTF-16 在发送与序列化替换前拒绝。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InvalidUnicode_IsRejectedBeforeSerialization(bool publicClient)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected HTTP")); var provider = new OllamaLlmProvider(new StubHttpClientFactory(handler));
        if (publicClient)
        {
            await using var client = OllamaFixture.Client(provider); var request = OllamaFixture.Request(); request.Messages[0]!["content"] = "\ud800";
            await Assert.ThrowsAsync<EncoderFallbackException>(() => client.LLM.OllamaChat.CompleteAsync(request, new() { SessionId = "unicode" }, TestContext.Current.CancellationToken));
        }
        else { var payload = OllamaFixture.Payload(); payload["future"] = "\ud800"; await Assert.ThrowsAsync<EncoderFallbackException>(() => provider.CompleteAsync(payload, OllamaFixture.Context(), TestContext.Current.CancellationToken)); }
        Assert.Equal(0, handler.Calls);
    }
    /// <summary>非法资源上限在配置阶段拒绝。</summary>
    [Theory] [InlineData(0, 1)] [InlineData(1, 0)] [InlineData(-1, 1)] [InlineData(1, -1)]
    public void InvalidLimits_AreRejected(int record, int response)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected HTTP"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OllamaLlmProvider(new StubHttpClientFactory(handler), options: new() { MaximumStreamRecordBytes = record, MaximumResponseBytes = response }));
    }
    /// <summary>声明长度的短读/长读都不能先交付看似完整的终态。</summary>
    [Theory] [InlineData(false, -1)] [InlineData(false, 1)] [InlineData(true, -1)] [InlineData(true, 1)]
    public async Task DeclaredLengthMismatch_PreventsTerminal(bool stream, int adjustment)
    {
        var bytes = Encoding.UTF8.GetBytes(OllamaFixture.Terminal); var body = new OllamaBodyStream(bytes);
        using var handler = new StubHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            response.Content.Headers.ContentType = new(stream ? "application/x-ndjson" : "application/json"); response.Content.Headers.ContentLength = bytes.Length + adjustment; return Task.FromResult(response);
        });
        var provider = new OllamaLlmProvider(new StubHttpClientFactory(handler)); var items = new List<OllamaChatEvent>();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { if (stream) { await foreach (var item in provider.StreamAsync(OllamaFixture.Payload(), OllamaFixture.Context(), TestContext.Current.CancellationToken)) items.Add(item); } else await provider.CompleteAsync(OllamaFixture.Payload(), OllamaFixture.Context(), TestContext.Current.CancellationToken); });
        Assert.Empty(items); Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>用未知长度碎片流测试响应，不依赖真实网络。</summary>
    private static StubHttpHandler Handler(Stream body, string media, int status = 200) => new((_, _) =>
    { var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new(media); return Task.FromResult(response); });
}
