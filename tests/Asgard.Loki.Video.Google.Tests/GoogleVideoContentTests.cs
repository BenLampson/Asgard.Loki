using System.Net;
using System.Text;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.Google.Tests.VeoFixture;

namespace Asgard.Loki.Video.Google.Tests;

/// <summary>内容传输只接受当前原生根地址，对重定向不转发 Key，不拥有调用者目标流。</summary>
public sealed class GoogleVideoContentTests
{
    /// <summary>按原始字节流式写入当前位置，不查询任务、不缓冲完整媒体、不关闭目标。</summary>
    [Fact]
    public async Task ContentCopy_PreservesBytesAndBorrowedOwnership()
    {
        var bytes = new byte[] { 0, 255, 3, 0, 128 }; var source = new MemoryStream(bytes);
        using var handler = new StubHttpHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal(ContentUri, request.RequestUri);
            Assert.Equal("fixture-veo-key", request.Headers.GetValues("x-goog-api-key").Single()); Assert.Null(request.Content);
            var content = new StreamContent(source); content.Headers.ContentType = new("video/mp4"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var factory = new GoogleTestHttpFactory(handler); var adapter = new GoogleVideoAdapter(factory, Options());
        using var destination = new MemoryStream(); destination.WriteByte(9);
        var copied = await adapter.CopyContentToAsync(ContentUri, destination, TestContext.Current.CancellationToken);
        Assert.Equal(5, copied); Assert.Equal(new byte[] { 9, 0, 255, 3, 0, 128 }, destination.ToArray());
        Assert.True(destination.CanWrite); Assert.False(source.CanRead); Assert.Equal(1, factory.ClientDisposals); Assert.Equal(0, factory.Disposals); Assert.Equal(1, handler.Calls);
    }
    /// <summary>显式内容重定向只执行 GET；API Key 只存在于首次已校验的根地址请求。</summary>
    [Theory] [InlineData(302)] [InlineData(303)] [InlineData(307)] [InlineData(308)]
    public async Task Redirect_DropsCredentialsAndPreservesSignedQuery(int status)
    {
        var calls = 0;
        using var handler = new StubHttpHandler((request, _) =>
        {
            if (++calls == 1)
            {
                Assert.True(request.Headers.Contains("x-goog-api-key")); var result = new HttpResponseMessage((HttpStatusCode)status); result.Headers.Location = new("https://storage.invalid/result.mp4?signature=fictional%2Fvalue"); return Task.FromResult(result);
            }
            Assert.Equal("https://storage.invalid/result.mp4?signature=fictional%2Fvalue", request.RequestUri!.AbsoluteUri); Assert.False(request.Headers.Contains("x-goog-api-key")); Assert.Null(request.Headers.Authorization);
            return Task.FromResult(Binary([1, 2, 3]));
        });
        var adapter = new GoogleVideoAdapter(new StubHttpClientFactory(handler), Options()); using var destination = new MemoryStream();
        Assert.Equal(3, await adapter.CopyContentToAsync(ContentUri, destination, TestContext.Current.CancellationToken)); Assert.Equal(2, handler.Calls);
    }
    /// <summary>任意 URI、其他版本或凭据查询不能触发带认证的请求。</summary>
    [Theory] [InlineData("https://other.invalid/v1beta/files/a:download?alt=media")]
    [InlineData("https://veo.invalid/v1/files/a:download?alt=media")]
    [InlineData("https://veo.invalid/v1beta/models/a:predictLongRunning")]
    [InlineData("https://veo.invalid/v1beta/files/a:download?key=wrong")]
    [InlineData("https://user@veo.invalid/v1beta/files/a:download?alt=media")]
    [InlineData("http://veo.invalid/v1beta/files/a:download?alt=media")]
    [InlineData("https://veo.invalid/v1beta/files/a%2fb:download?alt=media")]
    public async Task UntrustedInitialUri_FailsBeforeIo(string uri)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var adapter = new GoogleVideoAdapter(new StubHttpClientFactory(handler), Options()); using var destination = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.CopyContentToAsync(new(uri), destination, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>无限跳转、无地址、降级与带用户凭据的重定向均明确失败。</summary>
    [Theory] [InlineData("limit")] [InlineData("missing")] [InlineData("downgrade")] [InlineData("user-info")]
    public async Task InvalidRedirect_FailsWithoutFollowing(string kind)
    {
        using var handler = new StubHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            if (kind != "missing") response.Headers.Location = new(kind == "downgrade" ? "http://storage.invalid/video" : kind == "user-info" ? "https://user@storage.invalid/video" : "https://storage.invalid/video");
            return Task.FromResult(response);
        });
        var adapter = new GoogleVideoAdapter(new StubHttpClientFactory(handler), Options(redirects: kind == "limit" ? 0 : 3)); using var destination = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.CopyContentToAsync(ContentUri, destination, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls); Assert.Empty(destination.ToArray());
    }
    /// <summary>内容 API 错误保留原文，不把 JSON 错误正文当作视频写入。</summary>
    [Fact]
    public async Task ContentError_PreservesRawFailureAndDoesNotWriteDestination()
    {
        var body = Data().GetProperty("api_error"); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body, HttpStatusCode.Forbidden)));
        var adapter = new GoogleVideoAdapter(new StubHttpClientFactory(handler), Options()); using var destination = new MemoryStream();
        var error = await Assert.ThrowsAsync<VideoUpstreamException>(() => adapter.CopyContentToAsync(ContentUri, destination, TestContext.Current.CancellationToken));
        Assert.Equal(body.GetRawText(), Encoding.UTF8.GetString(error.ResponseBody.Span)); Assert.Empty(destination.ToArray()); Assert.Equal(1, handler.Calls);
    }
    /// <summary>成功响应的原始字节上限在传输前或读取中生效，失败不关闭目标流。</summary>
    [Fact]
    public async Task ContentLimit_IsAppliedToRawBytes()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Binary([0, 1, 2])));
        var adapter = new GoogleVideoAdapter(new StubHttpClientFactory(handler), Options(contentLimit: 2)); using var destination = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.CopyContentToAsync(ContentUri, destination, TestContext.Current.CancellationToken)); Assert.True(destination.CanWrite); Assert.Equal(1, handler.Calls);
    }
    /// <summary>完整字节或 JSON 已收到后，最后资源释放故障或取消仍会阻止成功返回。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task FinalDisposal_IsPartOfTheCall(bool content, bool cancel)
    {
        using var cancellation = new CancellationTokenSource(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(content
            ? Binary([1, 2, 3]) : Json(Query(4))));
        using var factory = new GoogleTestHttpFactory(handler) { OnClientDispose = () => { if (cancel) cancellation.Cancel(); else throw new IOException("fixture disposal"); } };
        var adapter = new GoogleVideoAdapter(factory, Options()); using var destination = new MemoryStream();
        async Task Call() { if (content) await adapter.CopyContentToAsync(ContentUri, destination, cancellation.Token); else await adapter.GetAsync(Operation, cancellation.Token); }
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Call); else await Assert.ThrowsAsync<IOException>(Call);
        Assert.Equal(1, handler.Calls); Assert.True(destination.CanWrite); Assert.Equal(0, factory.Disposals);
    }
    /// <summary>HTTP 200 的 JSON 错误正文不能写入视频目标。</summary>
    [Fact]
    public async Task JsonContentWithHttp200_IsNotVideoSuccess()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Data().GetProperty("api_error")))); var adapter = new GoogleVideoAdapter(new StubHttpClientFactory(handler), Options());
        using var destination = new MemoryStream(); var error = await Assert.ThrowsAsync<VideoUpstreamException>(() => adapter.CopyContentToAsync(ContentUri, destination, TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, error.StatusCode); Assert.Empty(destination.ToArray()); Assert.Equal(1, handler.Calls);
    }
    /// <summary>在内容端点的相对跳转也不转发凭据，整个调用不自动查询任务。</summary>
    [Fact]
    public async Task RelativeRedirect_IsBoundedAndDoesNotForwardKey()
    {
        var calls = 0; using var handler = new StubHttpHandler((request, _) =>
        {
            if (++calls == 1) { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("./next:download?alt=media", UriKind.Relative); return Task.FromResult(response); }
            Assert.Equal("https://veo.invalid/v1beta/files/next:download?alt=media", request.RequestUri!.AbsoluteUri); Assert.False(request.Headers.Contains("x-goog-api-key")); return Task.FromResult(Binary([1]));
        });
        var adapter = new GoogleVideoAdapter(new StubHttpClientFactory(handler), Options()); using var destination = new MemoryStream();
        Assert.Equal(1, await adapter.CopyContentToAsync(ContentUri, destination, TestContext.Current.CancellationToken)); Assert.Equal(2, handler.Calls);
    }
    /// <summary>预取消和不可写的目标在借用客户端之前失败。</summary>
    [Fact]
    public async Task InvalidDestinationAndCancellation_DoNotSend()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var adapter = new GoogleVideoAdapter(new StubHttpClientFactory(handler), Options());
        using var destination = new MemoryStream([1], false); await Assert.ThrowsAsync<ArgumentException>(() => adapter.CopyContentToAsync(ContentUri, destination, TestContext.Current.CancellationToken));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.CopyContentToAsync(ContentUri, destination, cancel.Token)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>构造原生视频二进制响应。</summary>
    private static HttpResponseMessage Binary(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes); content.Headers.ContentType = new("application/octet-stream"); return new(HttpStatusCode.OK) { Content = content };
    }
}
