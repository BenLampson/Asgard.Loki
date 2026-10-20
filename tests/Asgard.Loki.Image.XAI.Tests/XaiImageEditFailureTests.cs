using System.Net;
using System.Text;
using Xunit;

namespace Asgard.Loki.Image.XAI.Tests;

/// <summary>原生编辑错误、歧义来源、完整传输边界及取消所有权的必要风险测试。</summary>
public sealed class XaiImageEditFailureTests
{
    /// <summary>HTTP 和成功状态中的原生错误保留编辑专属错误类型与全部正文。</summary>
    [Theory]
    [InlineData(200)] [InlineData(400)]
    public async Task NativeErrorRetainsBodyWithoutRetry(int status)
    {
        var expected = XaiFixture.Part("error"); using var factory = new XaiTestFactory { OnSend = (_, _) => Task.FromResult(XaiFixture.Json(expected, (HttpStatusCode)status)) };
        var error = await Assert.ThrowsAsync<ImageEditUpstreamException>(() => new XaiImageEditAdapter(factory, XaiFixture.Options()).EditAsync(XaiFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.Equal(expected.GetRawText(), Encoding.UTF8.GetString(error.Body.Span)); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>HTTP 错误可为任意编码或二进制，编辑异常不丢字节也不隐式解码。</summary>
    [Fact]
    public async Task BinaryHttpErrorRemainsExactBytes()
    {
        byte[] body = [0, 255, 128, 13, 10]; using var factory = new XaiTestFactory { OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/octet-stream") } } }) };
        var error = await Assert.ThrowsAsync<ImageEditUpstreamException>(() => new XaiImageEditAdapter(factory, XaiFixture.Options()).EditAsync(XaiFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(body, error.Body.ToArray()); Assert.Equal(502, error.StatusCode); Assert.Equal("application/octet-stream", error.ContentType); Assert.Equal(1, factory.Calls);
    }
    /// <summary>成功响应必须使用原生 JSON/UTF-8，不能把 SSE 或其他编码解释成完整图片。</summary>
    [Theory]
    [InlineData("text/event-stream", "utf-8")]
    [InlineData("application/json", "iso-8859-1")]
    public async Task NonNativeSuccessTransportCannotSucceed(string media, string charset)
    {
        using var factory = new XaiTestFactory { OnSend = (_, _) => { var response = XaiFixture.Json(XaiFixture.Part("url")); response.Content.Headers.ContentType = new(media) { CharSet = charset }; return Task.FromResult(response); } };
        await Assert.ThrowsAsync<InvalidDataException>(() => new XaiImageEditAdapter(factory, XaiFixture.Options()).EditAsync(XaiFixture.Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>图片来源必须唯一、明确；不能把 OpenAI 引用或本地路径暗中改写为 xAI 输入。</summary>
    [Theory]
    [InlineData("missing")] [InlineData("both")] [InlineData("empty-array")]
    [InlineData("both-reference")] [InlineData("openai-reference")] [InlineData("file-url")]
    public void AmbiguousOrInvalidSourcesFailBeforeNetwork(string fault)
    {
        var request = XaiFixture.Request(); using var factory = new XaiTestFactory();
        switch (fault)
        {
            case "missing": request.Parameters.Remove("image"); break;
            case "both": request.Parameters["images"] = XaiFixture.Element("[{\"url\":\"https://example.invalid/a.png\"}]"); break;
            case "empty-array": request.Parameters.Remove("image"); request.Parameters["images"] = XaiFixture.Element("[]"); break;
            case "both-reference": request.Parameters["image"] = XaiFixture.Element("{\"url\":\"https://example.invalid/a.png\",\"file_id\":\"file-fixture\"}"); break;
            case "openai-reference": request.Parameters["image"] = XaiFixture.Element("{\"image_url\":\"https://example.invalid/a.png\"}"); break;
            case "file-url": request.Parameters["image"] = XaiFixture.Element("{\"url\":\"file:///private.png\"}"); break;
        }
        Assert.Throws<ArgumentException>(() => { _ = new XaiImageEditAdapter(factory, XaiFixture.Options()).EditAsync(request, TestContext.Current.CancellationToken); }); Assert.Equal(0, factory.Creates);
    }
    /// <summary>模型/prompt 覆盖、流式请求和请求超限均在 HTTP 前明确失败。</summary>
    [Theory]
    [InlineData("MODEL")] [InlineData("prompt")] [InlineData("stream")] [InlineData("limit")]
    public void InvalidBoundFieldsFailBeforeNetwork(string fault)
    {
        var request = XaiFixture.Request(); using var factory = new XaiTestFactory();
        if (fault != "limit") request.Parameters[fault] = XaiFixture.Element(fault == "stream" ? "true" : "\"override\"");
        var adapter = new XaiImageEditAdapter(factory, XaiFixture.Options(requestLimit: fault == "limit" ? 1 : 32768));
        if (fault == "stream") Assert.Throws<NotSupportedException>(() => { _ = adapter.EditAsync(request, TestContext.Current.CancellationToken); });
        else Assert.Throws<ArgumentException>(() => { _ = adapter.EditAsync(request, TestContext.Current.CancellationToken); }); Assert.Equal(0, factory.Creates);
    }
    /// <summary>损坏的响应容器不能被当作图片，已知字段类型仍有明确校验。</summary>
    [Theory]
    [InlineData("{}")] [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":[{}]}")] [InlineData("{\"data\":[{\"url\":false}]}")]
    public async Task InvalidNativeResultCannotSucceed(string json)
    {
        using var factory = new XaiTestFactory { OnSend = (_, _) => Task.FromResult(XaiFixture.Json(XaiFixture.Element(json))) };
        await Assert.ThrowsAsync<InvalidDataException>(() => new XaiImageEditAdapter(factory, XaiFixture.Options()).EditAsync(XaiFixture.Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>重复属性、无效 UTF-8、原始字节/声明长度错误不能返回完整成功。</summary>
    [Theory]
    [InlineData("duplicate")] [InlineData("utf8")] [InlineData("length-short")]
    [InlineData("length-long")] [InlineData("limit")]
    public async Task InvalidRawResponseCannotSucceed(string fault)
    {
        var bytes = Encoding.UTF8.GetBytes(XaiFixture.Part("url").GetRawText());
        if (fault == "utf8") bytes = [.. bytes, 0xff];
        if (fault == "duplicate") bytes = Encoding.UTF8.GetBytes("{\"data\":[],\"data\":[{\"url\":\"https://example.invalid/a.png\"}]}");
        using var factory = new XaiTestFactory { OnSend = (_, _) => { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; response.Content.Headers.ContentType = new("application/json"); if (fault.StartsWith("length-", StringComparison.Ordinal)) response.Content.Headers.ContentLength = bytes.Length + (fault == "length-short" ? -1 : 1); return Task.FromResult(response); } };
        await Assert.ThrowsAsync<InvalidDataException>(() => new XaiImageEditAdapter(factory, XaiFixture.Options(responseLimit: fault == "limit" ? 1 : 65536)).EditAsync(XaiFixture.Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>晚释放异常与取消仍中断整次编辑，不进行重发。</summary>
    [Theory]
    [InlineData("dispose")] [InlineData("cancel-before")] [InlineData("cancel-factory")]
    public async Task CancellationAndOwnedDisposalAreObserved(string fault)
    {
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); using var factory = new XaiTestFactory();
        if (fault == "dispose") factory.OnDisposeClient = () => throw new IOException("fixture disposal");
        if (fault == "cancel-before") canceled.Cancel(); if (fault == "cancel-factory") factory.OnCreate = canceled.Cancel;
        var adapter = new XaiImageEditAdapter(factory, XaiFixture.Options());
        if (fault == "dispose") await Assert.ThrowsAsync<IOException>(() => adapter.EditAsync(XaiFixture.Request(), canceled.Token));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.EditAsync(XaiFixture.Request(), canceled.Token)); Assert.Equal(fault == "dispose" ? 1 : 0, factory.Calls); Assert.Equal(fault == "cancel-before" ? 0 : 1, factory.Disposals); Assert.False(factory.Disposed);
    }
}
