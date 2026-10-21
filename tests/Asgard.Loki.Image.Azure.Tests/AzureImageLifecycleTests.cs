using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace Asgard.Loki.Image.Azure.Tests;

/// <summary>完整原生 HTTP 交换的失败/取消/释放不能先发布成功。</summary>
public sealed class AzureImageLifecycleTests
{
    /// <summary>读取或释放错误保持失败，图片输入继续由调用者持有。</summary>
    [Theory] [InlineData(false, "read")] [InlineData(true, "read")] [InlineData(false, "stream-dispose")] [InlineData(true, "stream-dispose")]
    [InlineData(false, "client-dispose")] [InlineData(true, "client-dispose")]
    public async Task ReadAndDisposalFailures_PreventSuccess(bool editing, string failure)
    {
        using var source = new AzureImageReadStream(Encoding.UTF8.GetBytes(AzureImageFixture.Part("result").GetRawText()))
        { FailAfter = failure == "read" ? 3 : null, FailDispose = failure == "stream-dispose" };
        using var factory = new AzureImageTestFactory { OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) }) };
        if (failure == "client-dispose") factory.OnClientDispose = () => throw new IOException("fixture client disposal failure");
        using var upload = new AzureImageReadStream([1, 2, 255]);
        await Assert.ThrowsAsync<IOException>(() => Call(factory, editing, upload, TestContext.Current.CancellationToken));
        Assert.True(source.Disposed); Assert.False(upload.Disposed); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.ClientDisposals); Assert.False(factory.Disposed);
    }

    /// <summary>调用前取消无 HTTP，EOF 和客户端释放时取消不能先返回成功。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task CancellationAtEveryFinalBoundary_IsHonored(bool editing, bool clientBoundary)
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); using var during = new CancellationTokenSource();
        using var source = new AzureImageReadStream(Encoding.UTF8.GetBytes(AzureImageFixture.Part("result").GetRawText())) { OnEof = clientBoundary ? null : during.Cancel };
        using var factory = new AzureImageTestFactory { OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) }) };
        if (clientBoundary) factory.OnClientDispose = during.Cancel;
        using var upload = new AzureImageReadStream([1]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(factory, editing, upload, cancelled.Token)); Assert.Equal(0, factory.Creates);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(factory, editing, upload, during.Token));
        Assert.True(source.Disposed); Assert.False(upload.Disposed); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.ClientDisposals);
    }

    /// <summary>声明长度、UTF-8、内容类型、字节策略和固定目标均在交付前检查。</summary>
    [Theory]
    [InlineData(false, "short")] [InlineData(true, "short")] [InlineData(false, "extra")] [InlineData(true, "extra")]
    [InlineData(false, "limit")] [InlineData(true, "limit")] [InlineData(false, "utf8")] [InlineData(true, "utf8")]
    [InlineData(false, "content-type")] [InlineData(true, "content-type")] [InlineData(false, "charset")] [InlineData(true, "charset")]
    [InlineData(false, "redirect")] [InlineData(true, "redirect")]
    public async Task InvalidTransport_PreventsResult(bool editing, string kind)
    {
        var bytes = kind == "utf8" ? new byte[] { 255 } : Encoding.UTF8.GetBytes(AzureImageFixture.Part("result").GetRawText());
        using var source = new AzureImageReadStream(bytes);
        using var factory = new AzureImageTestFactory { OnSend = (_, _) =>
        {
            var content = new StreamContent(source);
            if (kind == "short") content.Headers.ContentLength = bytes.Length + 1;
            if (kind == "extra") content.Headers.ContentLength = bytes.Length - 1;
            if (kind == "content-type") content.Headers.ContentType = new("text/event-stream");
            if (kind == "charset") content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-16" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content,
                RequestMessage = kind == "redirect" ? new HttpRequestMessage(HttpMethod.Get, "https://other.invalid/") : null });
        } };
        using var upload = new AzureImageReadStream([1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Call(factory, editing, upload, TestContext.Current.CancellationToken,
            AzureImageFixture.Options(responseLimit: kind == "limit" ? 1 : 100000)));
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.ClientDisposals); Assert.False(upload.Disposed);
    }

    /// <summary>HTTP 错误保留任意原始字节，业务错误保留完整过滤对象而不重写或掩盖。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task NativeFailure_RetainsCompleteBodyAndActualStatus(bool editing, bool business)
    {
        var bytes = business ? Encoding.UTF8.GetBytes(AzureImageFixture.Part("error").GetRawText()) : new byte[] { 255, 0, 0x7B };
        var status = business ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests;
        using var factory = new AzureImageTestFactory { OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) }) };
        using var upload = new AzureImageReadStream([1]);
        if (editing)
        {
            var error = await Assert.ThrowsAsync<ImageEditUpstreamException>(() => Call(factory, true, upload, TestContext.Current.CancellationToken));
            Assert.Equal((int)status, error.StatusCode); Assert.Equal(bytes, error.Body.ToArray()); Assert.Null(error.EditEvent);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ImageUpstreamException>(() => Call(factory, false, upload, TestContext.Current.CancellationToken));
            Assert.Equal((int)status, error.StatusCode); Assert.Equal(bytes, error.Body.ToArray()); Assert.Null(error.GenerationEvent);
        }
        Assert.Equal(1, factory.Calls); Assert.False(upload.Disposed);
    }

    /// <summary>调用者保留工厂，失败没有重发或把编辑变成生成。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task HttpFailure_HasOneAttemptAndNoFallback(bool editing)
    {
        using var factory = new AzureImageTestFactory { OnSend = (_, _) => throw new IOException("fixture send failure") };
        using var upload = new AzureImageReadStream([1]);
        await Assert.ThrowsAsync<IOException>(() => Call(factory, editing, upload, TestContext.Current.CancellationToken));
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.ClientDisposals); Assert.Equal(0, upload.Consumed); Assert.False(upload.Disposed); Assert.False(factory.Disposed);
    }

    private static Task<ImageGenerationResult> Call(IHttpClientFactory factory, bool editing, Stream upload, CancellationToken token, AzureImageOptions? options = null)
        => editing ? new AzureImageEditAdapter(factory, options ?? AzureImageFixture.Options()).EditMultipartAsync(new()
        { Model = "images", Prompt = "original", Images = [new() { Content = upload, FileName = "fixture.png", ContentType = "image/png" }] }, token)
        : new AzureImageAdapter(factory, options ?? AzureImageFixture.Options()).GenerateAsync(AzureImageFixture.Generation(), token);
}
