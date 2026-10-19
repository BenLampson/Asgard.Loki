using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Image.Tests;

/// <summary>编辑事件的成功判定属于原生适配器，核心只保护公开终态与源生命周期。</summary>
public sealed class ImageEditStreamTests
{
    /// <summary>未知事件即时保留，原生完成对象仅在正常读尽/释放后公开一次。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Completion_IsHeldUntilSourceDisposes(bool multipart)
    {
        using var one = new ObservedImageStream([1]); using var two = new ObservedImageStream([2]); using var mask = new ObservedImageStream([3]);
        var data = JsonSerializer.SerializeToElement(new { type = "unknown.completed", future = false });
        var unknown = new ImageEditEvent(data, false, new("pictures", "native-image-model", "edit-one"));
        var terminal = ImageEditFixture.Event(true); var adapter = new StubImageEditAdapter { Events = [unknown, terminal] };
        await using var client = ImageEditFixture.Build(adapter); List<ImageEditEvent> seen = [];
        await foreach (var item in Source(client.Image, multipart, one, two, mask, TestContext.Current.CancellationToken))
        { Assert.Equal(item.IsCompleted, adapter.IteratorDisposed); seen.Add(item); }
        Assert.Same(unknown, seen[0]); Assert.Same(terminal, seen[1]); Assert.Equal("wire-id", seen[1].EventId); Assert.Equal(42, seen[1].RetryMilliseconds);
        Assert.All(new[] { one, two, mask }, upload => { Assert.Equal(0, upload.Reads); Assert.False(upload.Disposed); });
    }

    /// <summary>空流、缺失/重复/晚到完成项和空事件不会先暴露成功。</summary>
    [Theory] [InlineData(false, "empty")] [InlineData(true, "empty")] [InlineData(false, "missing")] [InlineData(true, "missing")]
    [InlineData(false, "duplicate")] [InlineData(true, "duplicate")] [InlineData(false, "late")] [InlineData(true, "late")]
    [InlineData(false, "null")] [InlineData(true, "null")]
    public async Task InvalidSequence_WithholdsSuccess(bool multipart, string kind)
    {
        using var one = new ObservedImageStream([1]); using var two = new ObservedImageStream([2]); using var mask = new ObservedImageStream([3]);
        var partial = ImageEditFixture.Event(false); var done = ImageEditFixture.Event(true);
        var events = kind switch { "empty" => Array.Empty<ImageEditEvent>(), "missing" => [partial], "duplicate" => [partial, done, done], "late" => [partial, done, partial], _ => new ImageEditEvent[] { partial, null! } };
        var adapter = new StubImageEditAdapter { Events = events }; await using var client = ImageEditFixture.Build(adapter); List<ImageEditEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in Source(client.Image, multipart, one, two, mask, TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.All(seen, item => Assert.False(item.IsCompleted)); Assert.True(adapter.IteratorDisposed);
    }

    /// <summary>真正 DisposeAsync 失败必须阻止原始完成事件公开。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SourceDisposalFailure_OverridesHeldCompletion(bool multipart)
    {
        using var one = new ObservedImageStream([1]); using var two = new ObservedImageStream([2]); using var mask = new ObservedImageStream([3]);
        await using var client = ImageEditFixture.Build(new DisposalFaultImageEditAdapter()); List<ImageEditEvent> seen = [];
        await Assert.ThrowsAsync<IOException>(async () => { await foreach (var item in Source(client.Image, multipart, one, two, mask, TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.Single(seen); Assert.False(seen[0].IsCompleted);
    }

    /// <summary>晚到错误或取消同样不能被先前已构造的完成项掩盖。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task TailFailureOrCancellation_DoesNotPublishCompletion(bool multipart, bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var one = new ObservedImageStream([1]); using var two = new ObservedImageStream([2]); using var mask = new ObservedImageStream([3]);
        var adapter = new StubImageEditAdapter { TailFailure = cancel ? null : new IOException("fixture tail failed"), OnTail = cancel ? cancellation.Cancel : null };
        await using var client = ImageEditFixture.Build(adapter); List<ImageEditEvent> seen = [];
        var task = Collect(Source(client.Image, multipart, one, two, mask, cancellation.Token), seen);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); else await Assert.ThrowsAsync<IOException>(() => task);
        Assert.Single(seen); Assert.False(seen[0].IsCompleted); Assert.True(adapter.IteratorDisposed);
    }

    /// <summary>流式支持必须覆盖原配置全部目标，拒绝时不推进选择或回退普通调用。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MixedStreamingTargets_RejectBeforeSelection(bool multipart)
    {
        using var one = new ObservedImageStream([1]); using var two = new ObservedImageStream([2]); using var mask = new ObservedImageStream([3]);
        var streaming = new StubImageEditAdapter(); var nonstreaming = new NonStreamingImageEditAdapter();
        await using var client = ImageEditFixture.Build(streaming, nonstreaming);
        Assert.Throws<NotSupportedException>(() => Source(client.Image, multipart, one, two, mask, TestContext.Current.CancellationToken));
        Assert.Equal(0, streaming.Streams); Assert.Equal(0, nonstreaming.Calls);
        await client.Image.EditAsync(ImageEditFixture.Json(), TestContext.Current.CancellationToken); Assert.Equal(1, streaming.Edits);
    }

    /// <summary>提前停止只释放源枚举，不取走任何借用上传流或目标的释放权。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task EarlyStop_ReleasesEnumeratorOnly(bool multipart)
    {
        using var one = new ObservedImageStream([1]); using var two = new ObservedImageStream([2]); using var mask = new ObservedImageStream([3]);
        var adapter = new StubImageEditAdapter(); var client = ImageEditFixture.Build(adapter);
        await using (var iterator = Source(client.Image, multipart, one, two, mask, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        { Assert.True(await iterator.MoveNextAsync()); Assert.False(iterator.Current.IsCompleted); }
        Assert.True(adapter.IteratorDisposed); await client.DisposeAsync(); Assert.False(adapter.Disposed);
        Assert.All(new[] { one, two, mask }, upload => { Assert.Equal(0, upload.Reads); Assert.False(upload.Disposed); });
    }
    /// <summary>准备事件流时即复制原生元数据，枚举开始前修改输入不影响已绑定请求。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task StreamingSnapshots_PrecedeEnumeration(bool multipart)
    {
        using var one = new ObservedImageStream([1]); using var two = new ObservedImageStream([2]); using var mask = new ObservedImageStream([3]);
        var adapter = new StubImageEditAdapter(); await using var client = ImageEditFixture.Build(adapter); IAsyncEnumerable<ImageEditEvent> source;
        if (multipart)
        {
            var request = ImageEditFixture.Multipart(one, two, mask); var images = request.Images.ToList(); var fields = request.Fields.ToList();
            source = client.Image.StreamMultipartEditAsync(request with { Images = images, Fields = fields }, TestContext.Current.CancellationToken);
            images.Clear(); fields.Clear(); Assert.Equal(2, adapter.LastMultipart!.Images.Count); Assert.Equal(5, adapter.LastMultipart.Fields.Count);
            Assert.Same(mask, adapter.LastMultipart.Mask!.Content);
        }
        else
        {
            var request = ImageEditFixture.Json(); source = client.Image.StreamEditAsync(request, TestContext.Current.CancellationToken); request.Parameters.Clear();
            Assert.True(JsonElement.DeepEquals(ImageEditFixture.Golden("edit-native-request"), JsonSerializer.SerializeToElement(adapter.LastJson)));
        }
        List<ImageEditEvent> seen = []; await Collect(source, seen); Assert.True(seen[^1].IsCompleted);
    }

    /// <summary>公共入口的两种显式传输，不通过请求内容猜测是哪一种。</summary>
    private static IAsyncEnumerable<ImageEditEvent> Source(ImageClient client, bool multipart, Stream one, Stream two, Stream mask, CancellationToken token)
        => multipart ? client.StreamMultipartEditAsync(ImageEditFixture.Multipart(one, two, mask), token) : client.StreamEditAsync(ImageEditFixture.Json(), token);
    /// <summary>仅记录实际公开的事件。</summary>
    private static async Task Collect(IAsyncEnumerable<ImageEditEvent> source, List<ImageEditEvent> seen)
    { await foreach (var item in source) seen.Add(item); }
}
