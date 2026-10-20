using System.Net;
using System.Text.Json;

namespace Asgard.Loki.Video.Tests;

/// <summary>可选视频上传、派生及内容复制的独立内存测试目标，不执行网络请求。</summary>
internal sealed class StubVideoTransferAdapter(string id = "transfer") : IVideoMultipartTaskAdapter, IVideoRemixTaskAdapter, IVideoTaskContentAdapter
{
    /// <inheritdoc />
    public string Id { get; } = id;
    /// <inheritdoc />
    public string BindingIdentity { get; set; } = "fixture-server/model/credential/transfer-v1";
    /// <summary>上传等待边界，可检查快照及借用流。</summary>
    internal Func<VideoMultipartTaskRequest, CancellationToken, Task<VideoTaskSnapshot>>? OnMultipart { get; set; }
    /// <summary>派生等待边界，记录原始任务关联。</summary>
    internal Func<string, VideoTaskRequest, CancellationToken, Task<VideoTaskSnapshot>>? OnRemix { get; set; }
    /// <summary>内容复制边界，写入调用者明确提供的流。</summary>
    internal Func<string, VideoContentRequest, Stream, CancellationToken, Task<VideoContentResult>>? OnContent { get; set; }
    /// <summary>实际原生可选操作次数。</summary>
    internal int OptionalCalls { get; private set; }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> SubmitAsync(VideoTaskRequest request, CancellationToken cancellationToken = default) => Task.FromResult(Snapshot("source-" + Id));
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> GetAsync(string taskId, CancellationToken cancellationToken = default) => Task.FromResult(Snapshot(taskId));
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> SubmitAsync(VideoMultipartTaskRequest request, CancellationToken cancellationToken = default)
    { OptionalCalls++; return OnMultipart?.Invoke(request, cancellationToken) ?? Task.FromResult(Snapshot("multipart-" + Id)); }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> RemixAsync(string sourceTaskId, VideoTaskRequest request, CancellationToken cancellationToken = default)
    { OptionalCalls++; return OnRemix?.Invoke(sourceTaskId, request, cancellationToken) ?? Task.FromResult(Snapshot("remix-" + Id)); }
    /// <inheritdoc />
    public Task<VideoContentResult> CopyContentAsync(string taskId, VideoContentRequest request, Stream destination, CancellationToken cancellationToken = default)
    { OptionalCalls++; return OnContent?.Invoke(taskId, request, destination, cancellationToken) ?? Task.FromResult(Content(taskId)); }
    /// <summary>结果来自独立临时 JSON 窗口，完整数据由结果自己拥有。</summary>
    internal static VideoTaskSnapshot Snapshot(string id) => new(id, "queued", VideoTaskState.Queued, JsonSerializer.SerializeToElement(new { id, future = false }));
    /// <summary>复制完成元数据不表示新的模型任务终态。</summary>
    internal static VideoContentResult Content(string id, long count = 0) => new(id, count, HttpStatusCode.OK, new Dictionary<string, IReadOnlyList<string>> { ["Content-Type"] = new[] { "video/mp4" } });
}
