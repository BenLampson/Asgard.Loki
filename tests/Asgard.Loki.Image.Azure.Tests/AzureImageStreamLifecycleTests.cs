using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace Asgard.Loki.Image.Azure.Tests;

/// <summary>原生完整流的字节/EOF/资源边界不能先暴露图片完成或编辑成功。</summary>
public sealed class AzureImageStreamLifecycleTests
{
    /// <summary>尾部读取、流释放或客户端释放失败都会丢弃尚未交付的原生完成项。</summary>
    [Theory] [InlineData(false, "read")] [InlineData(true, "read")] [InlineData(false, "stream-dispose")] [InlineData(true, "stream-dispose")]
    [InlineData(false, "client-dispose")] [InlineData(true, "client-dispose")]
    public async Task ResourceFailure_NeverPublishesCompletion(bool editing, string failure)
    {
        var bytes = Encoding.UTF8.GetBytes(AzureImageStreamFixture.Wire(editing));
        using var source = new AzureImageReadStream(bytes) { FailAfter = failure == "read" ? bytes.Length : null, FailDispose = failure == "stream-dispose" };
        using var factory = new AzureImageTestFactory { OnSend = (_, _) => Task.FromResult(AzureImageStreamFixture.Response(source)) };
        if (failure == "client-dispose") factory.OnClientDispose = () => throw new IOException("fixture client disposal failure");
        using var upload = new AzureImageReadStream([1]); await using var client = AzureImageFixture.Build(factory, editing); var seen = new List<AzureImageStreamEvent>();
        await Assert.ThrowsAsync<IOException>(async () => { await foreach (var item in AzureImageStreamFixture.Read(client, editing, 1, [upload], null, TestContext.Current.CancellationToken)) seen.Add(item); });
        NoCompleted(seen); Assert.True(source.Disposed); Assert.False(upload.Disposed); Assert.False(factory.Disposed); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.ClientDisposals);
    }

    /// <summary>调用前、EOF 与客户端释放时取消均不能把缓存完成项当成功发布。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task CancellationBeforeAndAtFinalBoundary_PreventsCompletion(bool editing, bool atClient)
    {
        using var before = new CancellationTokenSource(); before.Cancel(); using var during = new CancellationTokenSource();
        using var source = new AzureImageReadStream(Encoding.UTF8.GetBytes(AzureImageStreamFixture.Wire(editing))) { OnEof = atClient ? null : during.Cancel };
        using var factory = new AzureImageTestFactory { OnSend = (_, _) => Task.FromResult(AzureImageStreamFixture.Response(source)) };
        if (atClient) factory.OnClientDispose = during.Cancel;
        using var upload = new AzureImageReadStream([1]); await using var client = AzureImageFixture.Build(factory, editing);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await foreach (var _ in AzureImageStreamFixture.Read(client, editing, 1, [upload], null, before.Token)) { } }); Assert.Equal(0, factory.Creates);
        var seen = new List<AzureImageStreamEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await foreach (var item in AzureImageStreamFixture.Read(client, editing, 1, [upload], null, during.Token)) seen.Add(item); });
        NoCompleted(seen); Assert.True(source.Disposed); Assert.False(upload.Disposed); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.ClientDisposals);
    }

    /// <summary>声明长度、完整原始字节上限、UTF-8、目标和内容类型在完成前严格验证。</summary>
    [Theory]
    [InlineData(false, "short")] [InlineData(true, "short")] [InlineData(false, "extra")] [InlineData(true, "extra")]
    [InlineData(false, "limit")] [InlineData(true, "limit")] [InlineData(false, "utf8")] [InlineData(true, "utf8")]
    [InlineData(false, "charset")] [InlineData(true, "charset")] [InlineData(false, "redirect")] [InlineData(true, "redirect")]
    public async Task InvalidTransport_NeverPublishesCompletion(bool editing, string kind)
    {
        var bytes = Encoding.UTF8.GetBytes(AzureImageStreamFixture.Wire(editing)); if (kind == "utf8") bytes = [.. bytes, 255];
        using var source = new AzureImageReadStream(bytes); using var factory = new AzureImageTestFactory { OnSend = (_, _) =>
        {
            var response = AzureImageStreamFixture.Response(source);
            if (kind == "short") response.Content.Headers.ContentLength = bytes.Length + 1;
            if (kind == "extra") response.Content.Headers.ContentLength = bytes.Length - 1;
            if (kind == "charset") response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream") { CharSet = "utf-16" };
            if (kind == "redirect") response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://other.invalid/");
            return Task.FromResult(response);
        } };
        using var upload = new AzureImageReadStream([1]); await using var client = AzureImageFixture.Build(factory, editing,
            AzureImageFixture.Options(responseLimit: kind == "limit" ? bytes.Length - 1 : 100000)); var seen = new List<AzureImageStreamEvent>();
        async Task Read() { await foreach (var item in AzureImageStreamFixture.Read(client, editing, 1, [upload], null, TestContext.Current.CancellationToken)) seen.Add(item); }
        if (kind == "utf8") await Assert.ThrowsAsync<DecoderFallbackException>(Read); else await Assert.ThrowsAsync<InvalidDataException>(Read);
        NoCompleted(seen); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.ClientDisposals); Assert.False(upload.Disposed);
    }

    /// <summary>上游完整 JSON 成功不能变成假 SSE；失败仍带有原始完整响应字节。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task JsonSuccess_IsNotProjectedToEvents(bool editing)
    {
        var original = AzureImageFixture.Part("result"); using var factory = new AzureImageTestFactory { OnSend = (_, _) => Task.FromResult(AzureImageFixture.Json(original)) };
        using var image = new AzureImageReadStream([1]); await using var client = AzureImageFixture.Build(factory, editing); var seen = new List<AzureImageStreamEvent>();
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in AzureImageStreamFixture.Read(client, editing, 1, [image], null, TestContext.Current.CancellationToken)) seen.Add(item); });
        var body = editing ? Assert.IsType<ImageEditUpstreamException>(error.InnerException).Body : Assert.IsType<ImageUpstreamException>(error.InnerException).Body;
        Assert.Equal(Encoding.UTF8.GetBytes(original.GetRawText()), body.ToArray()); Assert.Empty(seen); Assert.Equal(1, factory.Calls);
    }

    /// <summary>消费方早停会释放 HTTP 流，不虚构成功或关闭借用上传。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task EarlyStop_ReleasesTransportAndLeavesUploadBorrowed(bool editing)
    {
        using var source = new AzureImageReadStream(Encoding.UTF8.GetBytes(AzureImageStreamFixture.Wire(editing)));
        using var factory = new AzureImageTestFactory { OnSend = (_, _) => Task.FromResult(AzureImageStreamFixture.Response(source)) };
        using var image = new AzureImageReadStream([1]); await using var client = AzureImageFixture.Build(factory, editing);
        await foreach (var item in AzureImageStreamFixture.Read(client, editing, 1, [image], null, TestContext.Current.CancellationToken))
        { Assert.False(source.Eof); Assert.DoesNotContain("completed", item.Data.GetProperty("type").GetString()); break; }
        Assert.True(source.Disposed); Assert.Equal(1, factory.ClientDisposals); Assert.Equal(1, factory.Calls); Assert.False(factory.Disposed); Assert.False(image.Disposed);
    }

    /// <summary>HTTP/native JSON 失败无假事件，实际状态/完整字节被保留且没有重试。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task HttpAndJsonErrors_RetainNativeBody(bool editing, bool business)
    {
        var bytes = business ? Encoding.UTF8.GetBytes(AzureImageFixture.Part("error").GetRawText()) : new byte[] { 255, 0, 0x7B };
        using var factory = new AzureImageTestFactory { OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(business ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests)
        { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("application/json") } } }) };
        using var image = new AzureImageReadStream([1]); await using var client = AzureImageFixture.Build(factory, editing); var seen = new List<AzureImageStreamEvent>();
        async Task Read() { await foreach (var item in AzureImageStreamFixture.Read(client, editing, 1, [image], null, TestContext.Current.CancellationToken)) seen.Add(item); }
        if (editing) { var error = await Assert.ThrowsAsync<ImageEditUpstreamException>(Read); Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal(business ? 200 : 429, error.StatusCode); Assert.Null(error.EditEvent); }
        else { var error = await Assert.ThrowsAsync<ImageUpstreamException>(Read); Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal(business ? 200 : 429, error.StatusCode); Assert.Null(error.GenerationEvent); }
        Assert.Empty(seen); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.ClientDisposals);
    }

    internal static void NoCompleted(IEnumerable<AzureImageStreamEvent> seen)
    {
        Assert.DoesNotContain(seen, item => item.Data.GetProperty("type").GetString()!.EndsWith(".completed", StringComparison.Ordinal));
        Assert.DoesNotContain(seen, item => item.Completed == true);
    }
}
