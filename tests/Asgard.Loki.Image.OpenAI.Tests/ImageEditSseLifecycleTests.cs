using System.Text;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>原生编辑流的错误、尾部、取消和资源生命周期，不以已收到图片为由忽略后续失败。</summary>
public sealed class ImageEditSseLifecycleTests
{
    /// <summary>源 EOF/长度和流、响应内容、客户端释放都必须成功，才可公开原始完成项。</summary>
    [Theory]
    [InlineData(false, "read-tail")] [InlineData(true, "read-tail")] [InlineData(false, "short")] [InlineData(true, "short")]
    [InlineData(false, "long")] [InlineData(true, "long")] [InlineData(false, "stream-dispose")] [InlineData(true, "stream-dispose")]
    [InlineData(false, "response-dispose")] [InlineData(true, "response-dispose")] [InlineData(false, "client-dispose")] [InlineData(true, "client-dispose")]
    [InlineData(false, "cancel-tail")] [InlineData(true, "cancel-tail")]
    public async Task TerminalTailFailure_DoesNotPublishSuccess(bool multipart, string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var one = new EditReadStream([1]); using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        var bytes = Encoding.UTF8.GetBytes(EditSseFixture.Body);
        var stream = new EditReadStream(bytes)
        {
            FailAfterBytes = kind == "read-tail" ? bytes.Length : null, FailDispose = kind == "stream-dispose",
            OnEof = kind == "cancel-tail" ? cancellation.Cancel : null
        };
        using var handler = new StubHttpHandler((_, _) =>
        {
            if (kind == "response-dispose")
            {
                var content = new EditDisposalContent(stream); content.Headers.ContentType = new("text/event-stream");
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
            }
            return Task.FromResult(EditFixture.Response(stream, "text/event-stream", bytes.Length + (kind == "short" ? 1 : kind == "long" ? -1 : 0)));
        });
        using var factory = new EditTestFactory(handler) { FailClientDispose = kind == "client-dispose" };
        await using var client = EditFixture.Build(new OpenAiImageEditAdapter(factory, EditFixture.Options)); List<ImageEditEvent> seen = [];
        var task = Collect(EditSseFixture.Source(client, multipart, one, two, mask, cancellation.Token), seen);
        if (kind == "cancel-tail") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else if (kind is "short" or "long") await Assert.ThrowsAsync<InvalidDataException>(() => task);
        else await Assert.ThrowsAsync<IOException>(() => task);
        Assert.All(seen, item => Assert.False(item.IsCompleted)); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
        Assert.Equal(1, handler.Calls); Assert.All(new[] { one, two, mask }, upload => Assert.False(upload.Disposed)); Assert.False(factory.Disposed);
    }
    /// <summary>只在 GetAsyncEnumerator 提供的取消令牌同样传到 EOF 后检查，不能先发布暂存终态。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task EnumeratorCancellation_ReachesNativeTail(bool multipart)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var one = new EditReadStream([1]); using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        var stream = new EditReadStream(Encoding.UTF8.GetBytes(EditSseFixture.Body)) { OnEof = cancellation.Cancel };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(EditFixture.Response(stream, "text/event-stream")));
        using var factory = new EditTestFactory(handler); var adapter = new OpenAiImageEditAdapter(factory, EditFixture.Options);
        await using var iterator = EditSseFixture.Source(adapter, multipart, one, two, mask, CancellationToken.None).GetAsyncEnumerator(cancellation.Token);
        List<ImageEditEvent> seen = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { while (await iterator.MoveNextAsync()) seen.Add(iterator.Current); });
        Assert.All(seen, item => Assert.False(item.IsCompleted)); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>提前停止会释放响应/客户端，不强读到 EOF，不关闭调用者上传或借用工厂。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task EarlyStop_DisposesTransportAndLeavesUploadsOpen(bool multipart)
    {
        using var one = new EditReadStream([1]); using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        var stream = new EditReadStream(Encoding.UTF8.GetBytes(EditSseFixture.Body)) { FragmentSize = 1 };
        using var handler = new StubHttpHandler(async (message, token) =>
        { if (multipart) { using var wire = new MemoryStream(); await message.Content!.CopyToAsync(wire, token); } return EditFixture.Response(stream, "text/event-stream"); });
        using var factory = new EditTestFactory(handler); await using var client = EditFixture.Build(new OpenAiImageEditAdapter(factory, EditFixture.Options));
        await using (var iterator = EditSseFixture.Source(client, multipart, one, two, mask, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        { Assert.True(await iterator.MoveNextAsync()); Assert.False(iterator.Current.IsCompleted); }
        Assert.False(stream.Eof); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed);
        Assert.All(new[] { one, two, mask }, upload => Assert.False(upload.Disposed)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>即使原生错误晚于完成项，也保留完整原文和 SSE 身份并抑制成功。</summary>
    [Theory]
    [InlineData(false, "event")] [InlineData(true, "event")] [InlineData(false, "type")] [InlineData(true, "type")]
    [InlineData(false, "root")] [InlineData(true, "root")] [InlineData(false, "duplicate-null")] [InlineData(true, "duplicate-null")]
    public async Task NativeErrorAfterHeldTerminal_PreservesOriginalEvent(bool multipart, string kind)
    {
        var raw = kind switch
        {
            "type" => "{\"type\":\"error\",\"code\":\"native_error\",\"future\":{\"enabled\":false,\"value\":null}}",
            "root" => "{\"type\":\"vendor.native\",\"error\":{\"code\":\"invalid_mask\"},\"future\":false}",
            "duplicate-null" => "{\"type\":\"vendor.native\",\"error\":{\"code\":\"invalid_mask\"},\"error\":null}",
            _ => "{\"type\":\"vendor.native\",\"code\":\"native_error\",\"future\":{\"text\":\"😀\",\"value\":null}}"
        };
        var name = kind == "event" ? "error" : kind == "type" ? "message" : "vendor.native";
        var body = EditSseFixture.Partial + EditSseFixture.Completed + "event: " + name + "\nid: native-error\nretry: 25\ndata: " + raw + "\n\n";
        using var one = new EditReadStream([1]); using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(EditSseFixture.Response(body))); using var factory = new EditTestFactory(handler);
        await using var client = EditFixture.Build(new OpenAiImageEditAdapter(factory, EditFixture.Options)); List<ImageEditEvent> seen = [];
        var failure = await Assert.ThrowsAsync<ImageEditUpstreamException>(() => Collect(EditSseFixture.Source(client, multipart, one, two, mask, TestContext.Current.CancellationToken), seen));
        Assert.Equal(200, failure.StatusCode); Assert.Equal("text/event-stream; charset=utf-8", failure.ContentType); Assert.Equal(Encoding.UTF8.GetBytes(raw), failure.Body.ToArray());
        Assert.NotNull(failure.EditEvent); Assert.Equal(raw, failure.EditEvent.Data.GetRawText()); Assert.Equal(name, failure.EditEvent.EventName);
        Assert.Equal("native-error", failure.EditEvent.EventId); Assert.Equal(25, failure.EditEvent.RetryMilliseconds); Assert.False(failure.EditEvent.IsCompleted);
        Assert.Equal(new("pictures", "native-image-model", "edit-target-one"), failure.EditEvent.Metadata);
        Assert.All(seen, item => Assert.False(item.IsCompleted)); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>流式 HTTP/业务错误保留原始字节；普通 JSON 成功也不能降级改成伪事件。</summary>
    [Theory] [InlineData(false, "http")] [InlineData(true, "http")] [InlineData(false, "json-error")] [InlineData(true, "json-error")]
    [InlineData(false, "json-success")] [InlineData(true, "json-success")] [InlineData(false, "wrong-charset")] [InlineData(true, "wrong-charset")]
    public async Task NonSseResponse_IsNotConvertedToStream(bool multipart, string kind)
    {
        var bytes = kind == "http" ? new byte[] { 255, 128, 0, 13, 10 } : Encoding.UTF8.GetBytes(kind == "json-error" ? "{\"error\":{\"code\":\"native\"},\"future\":null}" : EditFixture.Text("edit-json-response"));
        var stream = new EditReadStream(bytes); var status = kind == "http" ? 503 : 200;
        var media = kind switch { "http" => "application/octet-stream", "wrong-charset" => "text/event-stream; charset=utf-16", _ => "application/json" };
        using var one = new EditReadStream([1]); using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(EditFixture.Response(stream, media, bytes.Length, status)));
        using var factory = new EditTestFactory(handler); await using var client = EditFixture.Build(new OpenAiImageEditAdapter(factory, EditFixture.Options));
        var task = EditSseFixture.Collect(EditSseFixture.Source(client, multipart, one, two, mask, TestContext.Current.CancellationToken));
        if (kind is "http" or "json-error")
        {
            var failure = await Assert.ThrowsAsync<ImageEditUpstreamException>(() => task);
            Assert.Equal(status, failure.StatusCode); Assert.Equal(bytes, failure.Body.ToArray()); Assert.Equal(media, failure.ContentType); Assert.Null(failure.EditEvent);
        }
        else await Assert.ThrowsAsync<InvalidDataException>(() => task);
        if (kind == "wrong-charset") Assert.Equal(0, stream.Reads);
        Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>UTF-8 错误不能在终态后被替换字符掩盖。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InvalidUtf8AfterTerminal_IsNotSuccessful(bool multipart)
    {
        var bytes = Encoding.UTF8.GetBytes(EditSseFixture.Completed).Concat(new byte[] { 255 }).ToArray();
        using var one = new EditReadStream([1]); using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(EditFixture.Response(new EditReadStream(bytes), "text/event-stream")));
        await using var client = EditFixture.Build(new OpenAiImageEditAdapter(new StubHttpClientFactory(handler), EditFixture.Options)); List<ImageEditEvent> seen = [];
        await Assert.ThrowsAsync<DecoderFallbackException>(() => Collect(EditSseFixture.Source(client, multipart, one, two, mask, TestContext.Current.CancellationToken), seen));
        Assert.Empty(seen); Assert.Equal(1, handler.Calls);
    }
    /// <summary>上传失败或上传取消不重试，不读取后续图片/遮罩，也不关闭任何借用源。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UploadFailure_DoesNotReplay(bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var one = new EditReadStream([0, 1, 2, 3]) { FailRead = !cancel, OnEof = cancel ? cancellation.Cancel : null };
        using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        using var handler = new StubHttpHandler(async (message, token) =>
        { using var wire = new MemoryStream(); await message.Content!.CopyToAsync(wire, token); return EditSseFixture.Response(); });
        using var factory = new EditTestFactory(handler); await using var client = EditFixture.Build(new OpenAiImageEditAdapter(factory, EditFixture.Options));
        var task = EditSseFixture.Collect(EditSseFixture.Source(client, true, one, two, mask, cancellation.Token));
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); else await Assert.ThrowsAsync<HttpRequestException>(() => task);
        Assert.Equal(0, two.Reads); Assert.Equal(0, mask.Reads); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
        Assert.All(new[] { one, two, mask }, upload => Assert.False(upload.Disposed));
    }
    /// <summary>有名但无效的 Content-Length 在读取前失败，不把无法解析的长度当作未声明。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InvalidDeclaredLength_FailsBeforeReading(bool multipart)
    {
        using var one = new EditReadStream([1]); using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        var stream = new EditReadStream(Encoding.UTF8.GetBytes(EditSseFixture.Body));
        using var handler = new StubHttpHandler((_, _) =>
        {
            var response = EditFixture.Response(stream, "text/event-stream"); Assert.True(response.Content.Headers.TryAddWithoutValidation("Content-Length", "not-a-number"));
            return Task.FromResult(response);
        });
        using var factory = new EditTestFactory(handler); await using var client = EditFixture.Build(new OpenAiImageEditAdapter(factory, EditFixture.Options));
        await Assert.ThrowsAsync<InvalidDataException>(() => EditSseFixture.Collect(EditSseFixture.Source(client, multipart, one, two, mask, TestContext.Current.CancellationToken)));
        Assert.Equal(0, stream.Reads); Assert.True(stream.Disposed); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>枚举到真实结束，保留此前已交付事件以断言没有完成标志。</summary>
    private static async Task Collect(IAsyncEnumerable<ImageEditEvent> source, List<ImageEditEvent> seen)
    { await foreach (var item in source) seen.Add(item); }
}
