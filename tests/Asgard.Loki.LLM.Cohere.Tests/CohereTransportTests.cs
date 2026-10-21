using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Cohere.Tests.CohereFixture;

namespace Asgard.Loki.LLM.Cohere.Tests;

/// <summary>原始 HTTP/字节/媒体/取消及目标边界，始终只有替代处理器。</summary>
public sealed class CohereTransportTests
{
    /// <summary>HTTP 错误保留原始二进制正文、状态和媒体类型，不按流模式猜测 JSON 错误格式。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task HttpError_KeepsRawStatusBodyAndContentType(bool stream)
    {
        byte[] bytes = [0xff, 0, 0x80, 10]; var body = new CohereBodyStream(bytes); using var handler = CohereNativeTests.Response(body, "application/octet-stream", 429); using var factory = new CohereHttpFactory(handler);
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(new(factory), stream));
        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode); Assert.Equal(bytes, failure.Body.ToArray()); Assert.Equal("application/octet-stream", failure.ContentType);
        Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>成功结果拒绝 SSE、其他媒体和非 UTF-8 声明，且不先消费正文。</summary>
    [Theory] [InlineData(false, "text/event-stream")] [InlineData(true, "text/event-stream")]
    [InlineData(false, "text/plain")] [InlineData(true, "text/plain")]
    [InlineData(false, "application/json; charset=utf-16")] [InlineData(true, "application/ndjson; charset=latin1")]
    public async Task WrongMedia_FailsBeforeReading(bool stream, string media)
    {
        var body = new CohereBodyStream([123, 125]); using var handler = new StubHttpHandler((_, _) =>
        { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(media); return Task.FromResult(response); });
        await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(new(new CohereHttpFactory(handler)), stream)); Assert.Equal(0, body.ReadCalls); Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>坏 UTF-8、未知转义、重复成员和不完整尾部都不能先交付流终态。</summary>
    [Theory] [InlineData(false, "utf8")] [InlineData(true, "utf8")] [InlineData(false, "unicode")] [InlineData(true, "unicode")]
    [InlineData(false, "duplicate")] [InlineData(true, "duplicate")] [InlineData(false, "truncated")] [InlineData(true, "truncated")]
    public async Task InvalidWire_IsNotRepaired(bool stream, string kind)
    {
        var valid = stream ? Start + "\n" + End + "\n" : "{\"text\":\"\"}";
        byte[] bytes = kind switch
        {
            "utf8" => [.. Encoding.UTF8.GetBytes(stream ? valid : "{\"text\":\""), 0xff],
            "unicode" => Encoding.UTF8.GetBytes(stream ? valid + "{\"event_type\":\"future\",\"unknown\":\"\\ud800\"}\n" : "{\"text\":\"\",\"unknown\":\"\\ud800\"}"),
            "duplicate" => Encoding.UTF8.GetBytes(stream ? valid + "{\"event_type\":\"future\",\"x\":1,\"x\":2}\n" : "{\"text\":\"\",\"unknown\":{\"x\":1,\"x\":2}}"),
            _ => Encoding.UTF8.GetBytes(stream ? valid + "{" : "{\"text\":")
        };
        using var handler = CohereNativeTests.Response(new CohereBodyStream(bytes)); var provider = new CohereLlmProvider(new CohereHttpFactory(handler)); var terminal = false;
        var failure = await Record.ExceptionAsync(async () => { if (stream) await foreach (var item in provider.StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken)) terminal |= item.IsTerminal; else await provider.CompleteAsync(Payload(), Context(), TestContext.Current.CancellationToken); });
        Assert.True(failure is InvalidDataException or JsonException, failure?.ToString()); Assert.False(terminal); Assert.Equal(1, handler.Calls);
    }
    /// <summary>完整结果只接受原生必填 text 和可选身份/结束原因的有效类型。</summary>
    [Theory] [InlineData("{}")] [InlineData("[]")] [InlineData("null")] [InlineData("{\"text\":null}")]
    [InlineData("{\"text\":0}")] [InlineData("{\"text\":\"\",\"response_id\":false}")] [InlineData("{\"text\":\"\",\"generation_id\":[]}")] [InlineData("{\"text\":\"\",\"finish_reason\":0}")]
    public async Task InvalidCompleteShape_FailsWithoutSynthesis(string json)
    {
        using var handler = CohereNativeTests.Response(new CohereBodyStream(Encoding.UTF8.GetBytes(json))); await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(new(new CohereHttpFactory(handler)), false)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>严格 UTF-8 字节预算的恰好边界成功，少一个字节即失败；不按字符估算或截断。</summary>
    [Theory] [InlineData("response", 0)] [InlineData("response", -1)] [InlineData("record", 0)] [InlineData("record", -1)]
    [InlineData("total", 0)] [InlineData("total", -1)] [InlineData("request", 0)] [InlineData("request", -1)]
    public async Task ExactByteLimits_AreEnforced(string kind, int adjustment)
    {
        var stream = kind is "record" or "total"; var text = stream ? Start + "\n" + End : "{\"text\":\"你好🌏\"}"; var bytes = Encoding.UTF8.GetBytes(text);
        var payload = Payload(); var wire = payload.DeepClone().AsObject(); wire["model"] = Target().UpstreamModel; wire["stream"] = stream;
        var options = kind switch
        {
            "response" => new CohereLlmOptions { MaximumResponseBytes = bytes.Length + adjustment },
            "record" => new CohereLlmOptions { MaximumStreamRecordBytes = Encoding.UTF8.GetByteCount(End) + adjustment },
            "total" => new CohereLlmOptions { MaximumStreamBytes = bytes.Length + adjustment },
            _ => new CohereLlmOptions { MaximumRequestBytes = Encoding.UTF8.GetByteCount(wire.ToJsonString()) + adjustment }
        };
        var body = new CohereBodyStream(bytes); using var handler = CohereNativeTests.Response(body); using var factory = new CohereHttpFactory(handler); var provider = new CohereLlmProvider(factory, options: options);
        if (adjustment == 0) await Invoke(provider, stream); else await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(provider, stream));
        Assert.Equal(kind == "request" && adjustment < 0 ? 0 : 1, handler.Calls); Assert.Equal(kind != "request" || adjustment == 0, body.Disposed);
    }
    /// <summary>声明长度短读、长读及无效文本都失败；已读 stream-end 不会先交付。</summary>
    [Theory] [InlineData(false, "short")] [InlineData(true, "short")] [InlineData(false, "long")] [InlineData(true, "long")]
    [InlineData(false, "invalid")] [InlineData(true, "invalid")] [InlineData(false, "over-limit")] [InlineData(true, "over-limit")]
    public async Task DeclaredLengthMismatch_PreventsCompletion(bool stream, string kind)
    {
        var bytes = Encoding.UTF8.GetBytes(stream ? Start + "\n" + End : "{\"text\":\"\"}"); var body = new CohereBodyStream(bytes);
        using var handler = new StubHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new("application/json");
            if (kind == "invalid") Assert.True(response.Content.Headers.TryAddWithoutValidation("Content-Length", "bad")); else response.Content.Headers.ContentLength = kind == "over-limit" ? long.MaxValue : bytes.Length + (kind == "short" ? -1 : 1); return Task.FromResult(response);
        });
        var provider = new CohereLlmProvider(new CohereHttpFactory(handler)); var terminal = false;
        await Assert.ThrowsAsync<InvalidDataException>(async () => { if (stream) await foreach (var item in provider.StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken)) terminal |= item.IsTerminal; else await Invoke(provider, false); });
        Assert.False(terminal); Assert.True(body.Disposed); if (kind is "invalid" or "over-limit") Assert.Equal(0, body.ReadCalls);
    }
    /// <summary>完整 JSON 的 EOF、释放和最终取消同样不能先返回完成对象。</summary>
    [Theory] [InlineData("read")] [InlineData("stream-dispose")] [InlineData("client-dispose")] [InlineData("cancel-eof")] [InlineData("cancel-stream")] [InlineData("cancel-client")]
    public async Task CompleteLateFailure_PreventsResult(string kind)
    {
        using var cancel = new CancellationTokenSource(); var body = new CohereBodyStream(Encoding.UTF8.GetBytes("{\"text\":\"\"}"))
        { FailAtEof = kind == "read", FailDispose = kind == "stream-dispose", OnEof = kind == "cancel-eof" ? cancel.Cancel : null, OnDispose = kind == "cancel-stream" ? cancel.Cancel : null };
        using var handler = CohereNativeTests.Response(body); using var factory = new CohereHttpFactory(handler)
        { OnClientDispose = kind == "client-dispose" ? () => throw new IOException("client") : kind == "cancel-client" ? cancel.Cancel : null };
        var failure = await Record.ExceptionAsync(() => new CohereLlmProvider(factory).CompleteAsync(Payload(), Context(), cancel.Token));
        if (kind.StartsWith("cancel", StringComparison.Ordinal)) Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure);
        Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>预取消不创建工厂客户端或发送请求。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PreCancellation_DoesNotCreateClient(bool stream)
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected")); using var factory = new CohereHttpFactory(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(new(factory), stream, cancel.Token)); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    internal static async Task Invoke(CohereLlmProvider provider, bool stream, CancellationToken? token = null)
    {
        if (stream) await foreach (var _ in provider.StreamAsync(Payload(), Context(), token ?? TestContext.Current.CancellationToken)) { }
        else await provider.CompleteAsync(Payload(), Context(), token ?? TestContext.Current.CancellationToken);
    }
}
