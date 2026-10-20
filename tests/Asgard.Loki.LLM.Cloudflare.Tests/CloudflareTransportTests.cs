using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Cloudflare.Tests.CloudflareFixture;

namespace Asgard.Loki.LLM.Cloudflare.Tests;

/// <summary>原生 HTTP 正文/媒体/字节/长度和资源释放，完全离线。</summary>
public sealed class CloudflareTransportTests
{
    /// <summary>非 JSON 二进制错误仍保留实际状态/原始字节/媒体，不按调用模式有损重编码。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RawHttpError_IsPreserved(bool stream)
    {
        byte[] bytes = [0xff, 0x80, 0, 10]; var body = new CloudflareBodyStream(bytes); using var handler = new StubHttpHandler((_, _) =>
        { var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new("application/octet-stream"); return Task.FromResult(response); });
        using var factory = new CloudflareHttpFactory(handler); var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(new(factory), stream));
        Assert.Equal(bytes, failure.Body.ToArray()); Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode); Assert.Equal("application/octet-stream", failure.ContentType); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生完整封装的显式错误不能因 HTTP 200 返回成文本成功，原始未知字段也保留。</summary>
    [Theory] [InlineData("{\"result\":null,\"success\":false,\"errors\":[],\"future\":null}")]
    [InlineData("{\"success\":true,\"errors\":[{\"code\":7,\"message\":\"native error\"}],\"future\":false}")]
    [InlineData("{\"error\":{\"message\":\"native error\"},\"future\":9007199254740993}")]
    public async Task Http200NativeError_IsNotNormalized(string json)
    {
        using var handler = Reply(new CloudflareBodyStream(Encoding.UTF8.GetBytes(json))); var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(new(new CloudflareHttpFactory(handler)), false));
        Assert.Equal(HttpStatusCode.OK, failure.StatusCode); Assert.Equal(json, failure.ResponseBody); Assert.Equal(1, handler.Calls);
    }
    /// <summary>明确文本 profile 不猜测或解包其他模型格式，缺失/错误必填字段明确失败。</summary>
    [Theory] [InlineData("{}")] [InlineData("[]")] [InlineData("null")]
    [InlineData("{\"response\":\"unwrapped\"}")] [InlineData("{\"result\":{\"response\":\"\"}}")]
    [InlineData("{\"result\":null,\"success\":true}")] [InlineData("{\"result\":{},\"success\":true}")]
    [InlineData("{\"result\":{\"response\":null},\"success\":true}")]
    [InlineData("{\"result\":{\"response\":0},\"success\":true}")]
    [InlineData("{\"result\":{\"response\":\"\",\"tool_calls\":{}},\"success\":true}")]
    [InlineData("{\"result\":{\"response\":\"\",\"tool_calls\":[null]},\"success\":true}")]
    [InlineData("{\"result\":{\"response\":\"\",\"tool_calls\":[{\"name\":0}]},\"success\":true}")]
    [InlineData("{\"result\":{\"response\":\"\"},\"success\":true,\"errors\":{}}")]
    public async Task InvalidCompleteEnvelope_FailsWithoutShapeGuessing(string json)
    {
        using var handler = Reply(new CloudflareBodyStream(Encoding.UTF8.GetBytes(json))); await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(new(new CloudflareHttpFactory(handler)), false)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>成功调用要求对应 UTF-8 JSON/SSE，错误媒体/字符集在正文读取前失败。</summary>
    [Theory] [InlineData(false, "text/event-stream")] [InlineData(true, "application/json")]
    [InlineData(false, "text/plain")] [InlineData(true, "text/plain")]
    [InlineData(false, "application/json; charset=utf-16")] [InlineData(true, "text/event-stream; charset=latin1")]
    public async Task WrongMedia_IsRejectedBeforeBody(bool stream, string type)
    {
        var body = new CloudflareBodyStream([123, 125]); using var handler = new StubHttpHandler((_, _) =>
        { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(type); return Task.FromResult(response); });
        await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(new(new CloudflareHttpFactory(handler)), stream)); Assert.Equal(0, body.ReadCalls); Assert.True(body.Disposed);
    }
    /// <summary>完整 JSON 的原始坏 UTF-8、重复字段或未知转义不能被替代字符/默认值修复。</summary>
    [Theory] [InlineData("utf8")] [InlineData("duplicate")] [InlineData("unicode")] [InlineData("truncated")]
    public async Task InvalidCompleteBytes_FailExplicitly(string kind)
    {
        byte[] bytes = kind switch
        {
            "utf8" => [.. Encoding.UTF8.GetBytes("{\"future\":\""), 0xff],
            "duplicate" => Encoding.UTF8.GetBytes(Complete[..^1] + ",\"future\":{\"x\":1,\"x\":2}}"),
            "unicode" => Encoding.UTF8.GetBytes(Complete[..^1] + ",\"future\":\"\\ud800\"}"), _ => Encoding.UTF8.GetBytes(Complete[..^1])
        };
        using var handler = Reply(new CloudflareBodyStream(bytes)); var failure = await Xunit.Record.ExceptionAsync(() => Invoke(new(new CloudflareHttpFactory(handler)), false)); Assert.True(failure is InvalidDataException or JsonException, failure?.ToString());
    }
    /// <summary>请求、完整正文及全 SSE 预算恰好边界成功，少一个原始字节便失败。</summary>
    [Theory] [InlineData("request", 0)] [InlineData("request", -1)] [InlineData("response", 0)] [InlineData("response", -1)] [InlineData("stream", 0)] [InlineData("stream", -1)]
    public async Task ExactByteBudget_CountsOriginalWire(string kind, int adjustment)
    {
        var stream = kind == "stream"; var payload = Payload(); payload["stream"] = stream; var bytes = Encoding.UTF8.GetBytes(stream ? Frame(NativeRecord) + Done : Complete);
        var options = kind switch
        { "request" => new CloudflareTextOptions { MaximumRequestBytes = Encoding.UTF8.GetByteCount(payload.ToJsonString()) + adjustment }, "response" => new CloudflareTextOptions { MaximumResponseBytes = bytes.Length + adjustment }, _ => new CloudflareTextOptions { MaximumStreamBytes = bytes.Length + adjustment } };
        var body = new CloudflareBodyStream(bytes); using var handler = Reply(body, stream); using var factory = new CloudflareHttpFactory(handler); var provider = new CloudflareTextProvider(factory, options: options);
        if (adjustment == 0) await Invoke(provider, stream); else await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(provider, stream));
        Assert.Equal(kind == "request" && adjustment < 0 ? 0 : 1, handler.Calls); Assert.Equal(kind != "request" || adjustment == 0, body.Disposed);
    }
    /// <summary>原始声明短读/长读、坏长度或超预算声明不能在看似 DONE 后返回成功。</summary>
    [Theory] [InlineData(false, "short")] [InlineData(true, "short")] [InlineData(false, "long")] [InlineData(true, "long")]
    [InlineData(false, "bad")] [InlineData(true, "bad")] [InlineData(false, "over")] [InlineData(true, "over")]
    public async Task DeclaredLength_PreventsFalseCompletion(bool stream, string kind)
    {
        var bytes = Encoding.UTF8.GetBytes(stream ? Frame(NativeRecord) + Done : Complete); var body = new CloudflareBodyStream(bytes);
        using var handler = new StubHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new(stream ? "text/event-stream" : "application/json");
            if (kind == "bad") response.Content.Headers.TryAddWithoutValidation("Content-Length", "bad"); else response.Content.Headers.ContentLength = kind == "over" ? long.MaxValue : bytes.Length + (kind == "short" ? -1 : 1); return Task.FromResult(response);
        });
        var provider = new CloudflareTextProvider(new CloudflareHttpFactory(handler)); var terminal = false;
        await Assert.ThrowsAsync<InvalidDataException>(async () => { if (stream) await foreach (var item in provider.StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken)) terminal |= item.IsTerminal; else await provider.CompleteAsync(Payload(), Context(), TestContext.Current.CancellationToken); });
        Assert.False(terminal); Assert.True(body.Disposed); if (kind is "bad" or "over") Assert.Equal(0, body.ReadCalls);
    }
    /// <summary>完整调用的 EOF/资源释放错误和最终取消也必须阻止结果交付。</summary>
    [Theory] [InlineData("read")] [InlineData("stream-dispose")] [InlineData("client-dispose")] [InlineData("cancel-eof")] [InlineData("cancel-stream")] [InlineData("cancel-client")]
    public async Task CompleteLateFailure_DoesNotReturnResult(string kind)
    {
        using var cancel = new CancellationTokenSource(); var body = new CloudflareBodyStream(Encoding.UTF8.GetBytes(Complete))
        { FailAtEof = kind == "read", FailDispose = kind == "stream-dispose", OnEof = kind == "cancel-eof" ? cancel.Cancel : null, OnDispose = kind == "cancel-stream" ? cancel.Cancel : null };
        using var handler = Reply(body); using var factory = new CloudflareHttpFactory(handler)
        { OnClientDispose = kind == "client-dispose" ? () => throw new IOException("fixture client") : kind == "cancel-client" ? cancel.Cancel : null };
        var failure = await Xunit.Record.ExceptionAsync(() => Invoke(new(factory), false, cancel.Token));
        if (kind.StartsWith("cancel", StringComparison.Ordinal)) Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure);
        Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>预取消不申请 HTTP 客户端、读取响应或自动重试。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PreCancelledCall_DoesNotCreateFactoryClient(bool stream)
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected")); using var factory = new CloudflareHttpFactory(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(new(factory), stream, cancel.Token)); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
}
