using System.Net;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Xunit;

namespace Asgard.Loki.Video.Tests;

/// <summary>可选原生上传、派生和内容契约的快照、完整目标绑定及借用资源测试。</summary>
public sealed class VideoTransferContractTests
{
    /// <summary>等待前复制有序重复字段和文件元数据，流从原位置借用且不被核心消费或关闭。</summary>
    [Fact]
    public async Task Multipart_SnapshotsMetadataAndBorrowsCurrentStream()
    {
        using var stream = new MemoryStream([1, 2, 3]); stream.Position = 1;
        var fields = new List<VideoMultipartField> { new("future[]", "false"), new("future[]", "0"), new("empty", "") };
        var uploads = new List<VideoUpload> { new() { Name = "input_reference", FileName = "fixture.png", Content = stream, ContentType = "image/png" } };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new StubVideoTransferAdapter { OnMultipart = async (request, ct) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(ct); Assert.Equal(["false", "0", ""], request.Fields.Select(field => field.Value));
            var upload = Assert.Single(request.Uploads); Assert.Same(stream, upload.Content); Assert.Equal("fixture.png", upload.FileName); Assert.Equal(1, stream.Position);
            return StubVideoTransferAdapter.Snapshot("uploaded");
        } };
        await using var loki = Build(adapter);
        var pending = loki.Video.SubmitAsync(new VideoMultipartTaskRequest { Model = "video", Fields = fields, Uploads = uploads }, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken); fields.Clear(); uploads.Clear(); release.SetResult(); var task = await pending;
        Assert.Equal("uploaded", task.Reference.TaskId); Assert.Equal(adapter.Id, task.Reference.TargetId); Assert.True(stream.CanRead); Assert.Equal(1, stream.Position);
    }
    /// <summary>派生和内容读取都使用源任务原目标，不能重新进入轮询选择；新引用仅替换任务 ID。</summary>
    [Fact]
    public async Task RemixAndContent_UseOriginalCompleteTarget()
    {
        var a = new StubVideoTransferAdapter("a"); var b = new StubVideoTransferAdapter("b");
        a.OnRemix = (id, request, _) => { Assert.Equal("source-a", id); Assert.Equal("video", request.Model); return Task.FromResult(StubVideoTransferAdapter.Snapshot("derived-a")); };
        a.OnContent = async (id, request, destination, ct) =>
        { Assert.Equal("source-a", id); Assert.Equal("thumbnail", Assert.Single(request.Query).Value); await destination.WriteAsync(new byte[] { 0xff, 0, 1 }, ct); return StubVideoTransferAdapter.Content(id, 3); };
        await using var loki = Build(a, b); var source = await loki.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        var derived = await loki.Video.RemixAsync(source.Reference, Request(), TestContext.Current.CancellationToken);
        Assert.Equal(source.Reference with { TaskId = "derived-a" }, derived.Reference);
        using var destination = new MemoryStream(); destination.WriteByte(8);
        var result = await loki.Video.CopyContentAsync(source.Reference, destination, new() { Query = [new("variant", "thumbnail")] }, TestContext.Current.CancellationToken);
        Assert.Equal(3, result.BytesWritten); Assert.Equal(new byte[] { 8, 0xff, 0, 1 }, destination.ToArray()); Assert.True(destination.CanWrite); Assert.Equal(0, b.OptionalCalls);
    }
    /// <summary>不支持的原生能力明确失败，不改发 JSON、重新选择上游或隐式查询状态。</summary>
    [Fact]
    public async Task UnsupportedOptionalOperations_DoNotFallBack()
    {
        var plain = new StubVideoAdapter(); var other = new StubVideoTransferAdapter(); await using var loki = Build(plain, other);
        await Assert.ThrowsAsync<NotSupportedException>(() => loki.Video.SubmitAsync(new VideoMultipartTaskRequest { Model = "video" }, TestContext.Current.CancellationToken));
        await using var onlyPlain = Build(plain); var source = await onlyPlain.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NotSupportedException>(() => onlyPlain.Video.RemixAsync(source.Reference, Request(), TestContext.Current.CancellationToken));
        using var destination = new MemoryStream();
        await Assert.ThrowsAsync<NotSupportedException>(() => onlyPlain.Video.CopyContentAsync(source.Reference, destination, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(plain.Queries); Assert.Equal(0, other.OptionalCalls); Assert.True(destination.CanWrite);
    }
    /// <summary>字段快照先于等待，内容查询的重复、空值及不透明文本保持顺序。</summary>
    [Fact]
    public async Task ContentQuery_SnapshotsBeforeAwait()
    {
        var query = new List<KeyValuePair<string, string>> { new("variant", "video"), new("future", ""), new("future", "opaque+/=") };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new StubVideoTransferAdapter { OnContent = async (id, request, _, ct) =>
        { entered.SetResult(); await release.Task.WaitAsync(ct); Assert.Equal(["video", "", "opaque+/="], request.Query.Select(field => field.Value)); return StubVideoTransferAdapter.Content(id); } };
        await using var loki = Build(adapter); var source = await loki.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken); using var destination = new MemoryStream();
        var pending = loki.Video.CopyContentAsync(source.Reference, destination, new() { Query = query }, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken); query.Clear(); release.SetResult(); await pending;
    }
    /// <summary>源引用配置摘要不匹配时，所有后续操作都在 IO 前失败。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ChangedReference_RejectsOptionalOperation(bool content)
    {
        var adapter = new StubVideoTransferAdapter(); await using var loki = Build(adapter); var source = await loki.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        var changed = source.Reference with { Fingerprint = "other" }; using var destination = new MemoryStream();
        if (content) await Assert.ThrowsAsync<InvalidOperationException>(() => loki.Video.CopyContentAsync(changed, destination, cancellationToken: TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => loki.Video.RemixAsync(changed, Request(), TestContext.Current.CancellationToken));
        Assert.Equal(0, adapter.OptionalCalls);
    }
    /// <summary>原生返回后发生配置变化或取消仍阻止交付，三个可选入口保持同一绑定规则。</summary>
    [Theory] [InlineData("multipart", false)] [InlineData("remix", false)] [InlineData("content", false)]
    [InlineData("multipart", true)] [InlineData("remix", true)] [InlineData("content", true)]
    public async Task FinalDriftOrCancellation_PreventsDelivery(string kind, bool cancel)
    {
        using var cancellation = new CancellationTokenSource(); var adapter = new StubVideoTransferAdapter(); await using var loki = Build(adapter);
        var source = await loki.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        void Change() { if (cancel) cancellation.Cancel(); else adapter.BindingIdentity = "changed-fixture-target"; }
        adapter.OnMultipart = (_, _) => { Change(); return Task.FromResult(StubVideoTransferAdapter.Snapshot("new")); };
        adapter.OnRemix = (_, _, _) => { Change(); return Task.FromResult(StubVideoTransferAdapter.Snapshot("new")); };
        adapter.OnContent = (id, _, _, _) => { Change(); return Task.FromResult(StubVideoTransferAdapter.Content(id)); };
        using var destination = new MemoryStream();
        Task Invoke() => kind switch
        {
            "multipart" => loki.Video.SubmitAsync(new VideoMultipartTaskRequest { Model = "video" }, cancellation.Token),
            "remix" => loki.Video.RemixAsync(source.Reference, Request(), cancellation.Token),
            _ => loki.Video.CopyContentAsync(source.Reference, destination, cancellationToken: cancellation.Token)
        };
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Invoke); else await Assert.ThrowsAsync<InvalidOperationException>(Invoke); Assert.Equal(1, adapter.OptionalCalls);
    }
    /// <summary>错误的派生任务及内容关联必须失败，不能重复源 ID 或接入其他任务的内容元数据。</summary>
    [Fact]
    public async Task WrongReturnedAssociations_AreRejected()
    {
        var adapter = new StubVideoTransferAdapter { OnRemix = (id, _, _) => Task.FromResult(StubVideoTransferAdapter.Snapshot(id)), OnContent = (_, _, _, _) => Task.FromResult(StubVideoTransferAdapter.Content("foreign")) };
        await using var loki = Build(adapter); var source = await loki.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken); using var destination = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => loki.Video.RemixAsync(source.Reference, Request(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => loki.Video.CopyContentAsync(source.Reference, destination, cancellationToken: TestContext.Current.CancellationToken)); Assert.True(destination.CanWrite);
        await Assert.ThrowsAsync<ArgumentException>(() => loki.Video.RemixAsync(source.Reference, Request() with { Model = "other" }, TestContext.Current.CancellationToken));
    }
    /// <summary>原生响应头保留多值、大小写无关查找和独立所有权，不将字节复制推断为模型状态。</summary>
    [Fact]
    public void ContentHeaders_AreImmutableIndependentSnapshots()
    {
        var values = new List<string> { "one", "two" }; var headers = new Dictionary<string, IReadOnlyList<string>> { ["X-Future"] = values };
        var result = new VideoContentResult("task", 3, HttpStatusCode.OK, headers); values.Clear(); headers.Clear();
        Assert.Equal(["one", "two"], result.Headers["x-future"]); Assert.Equal(3, result.BytesWritten); Assert.Equal("task", result.TaskId);
    }
    /// <summary>重复使用同一借用流会导致后续 part 空读，必须在上传前失败且不关闭流。</summary>
    [Fact]
    public async Task DuplicateUploadStream_IsRejectedBeforeAdapterCall()
    {
        using var stream = new MemoryStream([1, 2]); var upload = new VideoUpload { Name = "reference", FileName = "fixture.bin", Content = stream };
        var adapter = new StubVideoTransferAdapter(); await using var loki = Build(adapter);
        await Assert.ThrowsAsync<ArgumentException>(() => loki.Video.SubmitAsync(new VideoMultipartTaskRequest { Model = "video", Uploads = [upload, upload with { Name = "other" }] }, TestContext.Current.CancellationToken));
        Assert.Equal(0, adapter.OptionalCalls); Assert.True(stream.CanRead); Assert.Equal(0, stream.Position);
    }
    /// <summary>模型覆盖、控制字符和损坏 Unicode 都在派发前失败，不修改文件位置或文本。</summary>
    [Theory] [InlineData("model")] [InlineData("header")] [InlineData("filename")] [InlineData("unicode")]
    public async Task InvalidUploadMetadata_FailsBeforeAdapterCall(string kind)
    {
        using var stream = new MemoryStream([1]);
        var field = new VideoMultipartField(kind == "model" ? "model" : kind == "header" ? "bad\r\nfield" : "future", kind == "unicode" ? "\ud800" : "value");
        var upload = new VideoUpload { Name = "reference", FileName = kind == "filename" ? "bad\nfile" : "fixture.bin", Content = stream };
        var adapter = new StubVideoTransferAdapter(); await using var loki = Build(adapter);
        if (kind == "unicode") await Assert.ThrowsAsync<System.Text.EncoderFallbackException>(() => loki.Video.SubmitAsync(new VideoMultipartTaskRequest { Model = "video", Fields = [field], Uploads = [upload] }, TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<ArgumentException>(() => loki.Video.SubmitAsync(new VideoMultipartTaskRequest { Model = "video", Fields = [field], Uploads = [upload] }, TestContext.Current.CancellationToken));
        Assert.Equal(0, adapter.OptionalCalls); Assert.True(stream.CanRead); Assert.Equal(0, stream.Position);
    }
    /// <summary>内容复制失败后已写入的原生字节归调用者处理，不暗中清空、回卷或关闭目标流。</summary>
    [Fact]
    public async Task PartialContentFailure_LeavesBorrowedDestinationUnderCallerControl()
    {
        var adapter = new StubVideoTransferAdapter { OnContent = async (_, _, destination, ct) => { await destination.WriteAsync(new byte[] { 7, 8 }, ct); throw new IOException("fixture late content error"); } };
        await using var loki = Build(adapter); var source = await loki.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken); using var destination = new MemoryStream(); destination.WriteByte(6);
        await Assert.ThrowsAsync<IOException>(() => loki.Video.CopyContentAsync(source.Reference, destination, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(destination.CanWrite); Assert.Equal(new byte[] { 6, 7, 8 }, destination.ToArray()); Assert.Equal(3, destination.Position);
    }
    /// <summary>不可写目标在供应商调用前拒绝，不能通过隐式创建缓存流掩盖问题。</summary>
    [Fact]
    public async Task ReadOnlyContentDestination_IsRejectedBeforeIo()
    {
        var adapter = new StubVideoTransferAdapter(); await using var loki = Build(adapter); var source = await loki.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken); using var destination = new MemoryStream([1], false);
        await Assert.ThrowsAsync<ArgumentException>(() => loki.Video.CopyContentAsync(source.Reference, destination, cancellationToken: TestContext.Current.CancellationToken)); Assert.Equal(0, adapter.OptionalCalls);
    }
    /// <summary>建立完整模块入口，保持目标顺序可验证而不触发实际传输。</summary>
    private static ILokiClient Build(params IVideoTaskAdapter[] targets) => new LokiClientBuilder().AddVideo(new() { Routes = [new() { Model = "video", Targets = targets }] }).Build();
    /// <summary>原生 JSON 参数由能力入口复制，不建立统一提示词模式。</summary>
    private static VideoTaskRequest Request() => new() { Model = "video", Parameters = new JsonObject { ["prompt"] = "fixture", ["Future"] = false } };
}
