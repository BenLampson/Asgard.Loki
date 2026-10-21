using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Image.Volcengine.Tests;

/// <summary>原生业务和签名错误、明确不支持、编码及资源终态边界。</summary>
public sealed class JimengImageFailureTests
{
    /// <summary>HTTP 成功也可能是原生失败；原始错误与状态不改写。</summary>
    [Theory]
    [InlineData("business_error", 200)] [InlineData("business_error", 400)]
    [InlineData("signature_error", 200)] [InlineData("signature_error", 403)]
    public async Task NativeFailureKeepsCompleteBytesAndNeverRetries(string name, int status)
    {
        using var factory = JimengFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(JimengFixture.Json(JimengFixture.Part(name), (HttpStatusCode)status));
        var error = await Assert.ThrowsAsync<ImageUpstreamException>(() => JimengFixture.Adapter(factory).GenerateAsync(JimengFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.True(JsonElement.DeepEquals(JimengFixture.Part(name), JimengFixture.Element(error.ResponseBody)));
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }
    /// <summary>HTTP 错误的非 UTF-8 字节不先解码为 JSON。</summary>
    [Fact]
    public async Task HttpFailureRetainsBinaryBody()
    {
        byte[] original = [255, 0, 254]; using var factory = JimengFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new ByteArrayContent(original) });
        var error = await Assert.ThrowsAsync<ImageUpstreamException>(() => JimengFixture.Adapter(factory).GenerateAsync(JimengFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(original, error.Body.ToArray()); Assert.Equal(1, factory.Calls);
    }
    /// <summary>失败的响应结构不能误报为成功图片。</summary>
    [Theory]
    [InlineData("{\"code\":\"10000\",\"data\":{}}")]
    [InlineData("{\"code\":10000}")]
    [InlineData("{\"code\":10000,\"data\":{\"image_urls\":[null]}}")]
    [InlineData("{\"code\":10000,\"data\":{\"binary_data_base64\":{}}}")]
    [InlineData("{\"code\":10000,\"data\":{},\"future\":1,\"future\":2}")]
    public async Task InvalidNativeEnvelopeFailsExplicitly(string json)
    {
        using var factory = JimengFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        await Assert.ThrowsAsync<InvalidDataException>(() => JimengFixture.Adapter(factory).GenerateAsync(JimengFixture.Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>固定 req_key 和原始 prompt 不允许扩展大小写覆盖。</summary>
    [Theory]
    [InlineData("req_key")] [InlineData("REQ_KEY")] [InlineData("Prompt")] [InlineData("model")]
    public void OverridesFailBeforeHttp(string name)
    {
        using var factory = JimengFixture.Factory(); var request = JimengFixture.Request(); request.Parameters[name] = JsonSerializer.SerializeToElement("different");
        Assert.Throws<ArgumentException>(() => { _ = JimengFixture.Adapter(factory).GenerateAsync(request, TestContext.Current.CancellationToken); }); Assert.Equal(0, factory.Creates);
    }
    /// <summary>没有原生 SSE 的同步操作不能合成流或调用另一端点。</summary>
    [Fact]
    public void UnsupportedStreamingFailsBeforeHttp()
    {
        using var factory = JimengFixture.Factory(); var adapter = JimengFixture.Adapter(factory); var request = JimengFixture.Request();
        Assert.Throws<NotSupportedException>(() => adapter.StreamAsync(request, TestContext.Current.CancellationToken)); request.Parameters["stream"] = JsonSerializer.SerializeToElement(true);
        Assert.Throws<NotSupportedException>(() => { _ = adapter.GenerateAsync(request, TestContext.Current.CancellationToken); }); Assert.Equal(0, factory.Creates);
    }
    /// <summary>请求在本地字节上限或编码失败时不创建 HTTP 资源。</summary>
    [Fact]
    public void RequestLimitsAndInvalidUnicodeFailBeforeHttp()
    {
        using var factory = JimengFixture.Factory();
        Assert.Throws<ArgumentException>(() => { _ = JimengFixture.Adapter(factory, JimengFixture.Options(requestLimit: 1)).GenerateAsync(JimengFixture.Request(), TestContext.Current.CancellationToken); });
        var request = JimengFixture.Request() with { Prompt = "\ud800" };
        Assert.Throws<EncoderFallbackException>(() => { _ = JimengFixture.Adapter(factory).GenerateAsync(request, TestContext.Current.CancellationToken); }); Assert.Equal(0, factory.Creates);
    }
    /// <summary>限制在完整原始字节上执行，不以解析后字符串长度代替。</summary>
    [Theory]
    [InlineData("bytes")] [InlineData("length")] [InlineData("utf8")] [InlineData("content-type")]
    public async Task RawResponseBoundariesAreEnforced(string kind)
    {
        var bytes = Encoding.UTF8.GetBytes(JimengFixture.Part("url_result").GetRawText()); if (kind == "utf8") bytes = [255];
        using var source = new ArkReadStream(bytes); using var factory = JimengFixture.Factory(); factory.OnSend = (_, _) =>
        {
            var content = new StreamContent(source); content.Headers.ContentType = new(kind == "content-type" ? "text/event-stream" : "application/json");
            if (kind == "length") content.Headers.ContentLength = bytes.Length + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => JimengFixture.Adapter(factory, JimengFixture.Options(responseLimit: kind == "bytes" ? bytes.Length - 1 : bytes.Length + 100)).GenerateAsync(JimengFixture.Request(), TestContext.Current.CancellationToken));
        Assert.True(source.Disposed); Assert.Equal(1, factory.Disposals); Assert.Equal(1, factory.Calls);
    }
    /// <summary>最终读取、释放或取消失败不能交付已解析的成功结果。</summary>
    [Theory]
    [InlineData("read")] [InlineData("dispose")] [InlineData("client")] [InlineData("cancel")]
    public async Task LateFailuresCannotReleaseSuccess(string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var source = new ArkReadStream(Encoding.UTF8.GetBytes(JimengFixture.Part("url_result").GetRawText()))
        { FailRead = kind == "read", FailDispose = kind == "dispose", OnEof = kind == "cancel" ? cancellation.Cancel : null };
        using var factory = JimengFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) });
        if (kind == "client") factory.OnDisposeClient = () => throw new IOException("fixture client disposal");
        if (kind == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JimengFixture.Adapter(factory).GenerateAsync(JimengFixture.Request(), cancellation.Token));
        else await Assert.ThrowsAsync<IOException>(() => JimengFixture.Adapter(factory).GenerateAsync(JimengFixture.Request(), cancellation.Token));
        Assert.True(source.Disposed); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }
    /// <summary>完整地址不允许路径前缀、明文、查询或内嵌凭据改变签名目标。</summary>
    [Theory]
    [InlineData("http://visual.example.invalid/")] [InlineData("https://visual.example.invalid/prefix/")]
    [InlineData("https://visual.example.invalid/?other=1")] [InlineData("https://user:password@visual.example.invalid/")]
    public void InvalidRootFailsBeforeFactory(string address)
    {
        using var factory = JimengFixture.Factory(); Assert.Throws<ArgumentException>(() => JimengFixture.Adapter(factory, JimengFixture.Options(root: address))); Assert.Equal(0, factory.Creates);
    }
}
