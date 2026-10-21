using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>完整结果必须通过严格编码、有限正文、正常 EOF 和资源释放后才能成功。</summary>
public sealed class JsonTransportTests
{
    /// <summary>HTTP 错误保留状态和原始正文，不自动重试或切换认证。</summary>
    [Theory] [InlineData(400, false)] [InlineData(401, false)] [InlineData(429, true)] [InlineData(503, true)] [InlineData(200, false)] [InlineData(200, true)]
    public async Task UpstreamErrors_PreserveStatusAndBody(int status, bool responses)
    {
        const string body = "{\"error\":{\"code\":\"fixture\",\"message\":\"限制\",\"future\":null}}";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        var factory = new AzureHttpFactory(handler); var provider = new AzureLlmProvider(factory);
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => Call(provider, responses));
        Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.Equal(body, error.ResponseBody); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>非法正文不因未知字段、错误媒体类型或宽松 UTF-8 解码被接受。</summary>
    [Theory] [InlineData("duplicate")] [InlineData("truncated")] [InlineData("escape")] [InlineData("encoding")] [InlineData("array")] [InlineData("media")]
    public async Task InvalidBody_DoesNotProduceResult(string failure)
    {
        var bytes = failure switch
        {
            "duplicate" => Encoding.UTF8.GetBytes("{\"id\":\"one\",\"future\":{\"x\":1,\"x\":2}}"),
            "truncated" => Encoding.UTF8.GetBytes("{\"id\":\"one\""),
            "escape" => Encoding.UTF8.GetBytes("{\"future\":\"\\ud800\"}"),
            "encoding" => new byte[] { 123, 34, 120, 34, 58, 34, 255, 34, 125 },
            "array" => Encoding.UTF8.GetBytes("[]"), _ => Encoding.UTF8.GetBytes(AzureFixture.Response(false))
        };
        var stream = new OllamaBodyStream(bytes);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(stream, failure == "media" ? "text/event-stream" : "application/json")));
        var factory = new AzureHttpFactory(handler); var provider = new AzureLlmProvider(factory);
        if (failure is "duplicate" or "truncated") await Assert.ThrowsAnyAsync<JsonException>(() => Call(provider, false));
        else if (failure == "encoding") await Assert.ThrowsAsync<DecoderFallbackException>(() => Call(provider, false));
        else await Assert.ThrowsAsync<InvalidDataException>(() => Call(provider, false));
        Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>限制按照原始 UTF-8 字节计算，精确边界可用，少一个字节必须失败。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task BodyLimit_IsExactAndByteBased(bool responses, bool tooSmall)
    {
        var bytes = Encoding.UTF8.GetBytes(AzureFixture.Response(responses)); var stream = new OllamaBodyStream(bytes);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(stream))); var factory = new AzureHttpFactory(handler);
        var provider = new AzureLlmProvider(factory, new() { MaximumBodyBytes = bytes.Length - (tooSmall ? 1 : 0) });
        if (tooSmall) await Assert.ThrowsAsync<InvalidDataException>(() => Call(provider, responses)); else { await Call(provider, responses); Assert.True(stream.Eof); }
        Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>声明长度偏大、偏小均不得接受被截断或额外追加的结果。</summary>
    [Theory] [InlineData(-1)] [InlineData(1)]
    public async Task ContentLength_MustMatchActualBytes(int difference)
    {
        var bytes = Encoding.UTF8.GetBytes(AzureFixture.Response(false)); var stream = new OllamaBodyStream(bytes);
        using var handler = new StubHttpHandler((_, _) => { var response = Reply(stream); response.Content.Headers.ContentLength = bytes.Length + difference; return Task.FromResult(response); });
        var factory = new AzureHttpFactory(handler); await Assert.ThrowsAsync<InvalidDataException>(() => Call(new AzureLlmProvider(factory), false)); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>读尾错误、释放错误和尾部取消均发生在公开成功之前。</summary>
    [Theory] [InlineData("read")] [InlineData("body-dispose")] [InlineData("client-dispose")] [InlineData("eof-cancel")] [InlineData("body-cancel")] [InlineData("client-cancel")]
    public async Task LateFailure_PreventsSuccessfulReturn(string failure)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(AzureFixture.Response(false))) { FailAtEof = failure == "read", FailDispose = failure == "body-dispose", OnEof = failure == "eof-cancel" ? cancel.Cancel : null, OnDispose = failure == "body-cancel" ? cancel.Cancel : null };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(stream)));
        var factory = new AzureHttpFactory(handler) { OnDispose = () => { if (failure == "client-dispose") throw new IOException("client dispose"); if (failure == "client-cancel") cancel.Cancel(); } };
        var provider = new AzureLlmProvider(factory);
        if (failure.EndsWith("cancel", StringComparison.Ordinal)) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(AzureFixture.Request(false), AzureFixture.Context(false), cancel.Token));
        else await Assert.ThrowsAsync<IOException>(() => provider.CompleteAsync(AzureFixture.Request(false), AzureFixture.Context(false), cancel.Token));
        Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>预先取消不取令牌、不创建 HTTP 客户端。</summary>
    [Fact]
    public async Task PreCancelled_DoesNotAcquireResources()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); var clock = new AzureClock(); var source = new AzureTokenSource(clock);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(AzureFixture.Reply(false))); var factory = new AzureHttpFactory(handler); var provider = new AzureLlmProvider(factory, tokenSource: source, timeProvider: clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(AzureFixture.Request(false), AzureFixture.Context(false, external: true), cancel.Token)); Assert.Empty(factory.Clients); Assert.Equal(0, source.Calls); Assert.Equal(0, handler.Calls);
    }
    private static async Task Call(AzureLlmProvider provider, bool responses)
    {
        if (responses) await provider.CreateAsync(AzureFixture.Request(true), AzureFixture.Context(true), TestContext.Current.CancellationToken);
        else await provider.CompleteAsync(AzureFixture.Request(false), AzureFixture.Context(false), TestContext.Current.CancellationToken);
    }
    private static HttpResponseMessage Reply(Stream stream, string media = "application/json")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }; response.Content.Headers.ContentType = new(media); return response;
    }
}
