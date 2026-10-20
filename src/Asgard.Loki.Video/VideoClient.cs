using Asgard.Loki.Video.Internal;

namespace Asgard.Loki.Video;

/// <summary>仅协调 Video 原生提交和显式查询，任务绑定独立于 LLM Session 与其他能力。</summary>
public sealed class VideoClient
{
    private readonly Dictionary<string, VideoRouteState> _routes;
    /// <summary>每个客户端持有独立的首次选择状态。</summary>
    internal VideoClient(VideoConfiguration configuration)
        => _routes = configuration.Routes.ToDictionary(pair => pair.Key, pair => new VideoRouteState(pair.Value), StringComparer.Ordinal);
    /// <summary>提交一次原生任务，返回可持久化的完整目标引用；不启动后台查询。</summary>
    public Task<VideoTask> SubmitAsync(VideoTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        var snapshot = request.Snapshot();
        var target = Route(snapshot.Model).Select(); target.Validate();
        return SubmitCoreAsync(snapshot, target, cancellationToken);
    }
    /// <summary>先固定原始参数/目标，调用后再次检查身份，不把配置漂移的结果作为绑定任务公开。</summary>
    private static async Task<VideoTask> SubmitCoreAsync(VideoTaskRequest request, VideoTarget target, CancellationToken cancellationToken)
    {
        var result = await target.Adapter.SubmitAsync(request, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("视频适配器未返回原生任务。");
        target.Validate(); cancellationToken.ThrowIfCancellationRequested();
        return new(new(request.Model, target.Id, target.Fingerprint, result.TaskId), result);
    }
    /// <summary>查询一次原生任务，返回其当前状态、错误及完整结果；由调用者决定再次查询或读取结果地址。</summary>
    public Task<VideoTask> GetAsync(VideoTaskReference reference, CancellationToken cancellationToken = default)
    {
        Reference(reference);
        cancellationToken.ThrowIfCancellationRequested();
        var target = Route(reference.Model).Resolve(reference);
        return GetCoreAsync(reference, target, cancellationToken);
    }
    /// <summary>选择一个完整目标并提交一次原生 multipart，不支持时不降级或切换供应商。</summary>
    public Task<VideoTask> SubmitAsync(VideoMultipartTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested(); var snapshot = request.Snapshot();
        var target = Route(snapshot.Model).Select(); target.Validate();
        if (target.Adapter is not IVideoMultipartTaskAdapter adapter) throw new NotSupportedException("视频原目标不支持原生 multipart 提交。");
        return SubmitMultipartAsync(snapshot, target, adapter, cancellationToken);
    }
    /// <summary>将新任务引用绑定到完成实际上传的同一完整目标。</summary>
    private static async Task<VideoTask> SubmitMultipartAsync(VideoMultipartTaskRequest request, VideoTarget target, IVideoMultipartTaskAdapter adapter, CancellationToken cancellationToken)
    {
        var result = await adapter.SubmitAsync(request, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("视频适配器未返回原生任务。");
        target.Validate(); cancellationToken.ThrowIfCancellationRequested(); return new(new(request.Model, target.Id, target.Fingerprint, result.TaskId), result);
    }
    /// <summary>在源任务的完整目标上明确派生原生 remix，不轮询、重新调度或借用另一模型配置。</summary>
    public Task<VideoTask> RemixAsync(VideoTaskReference source, VideoTaskRequest request, CancellationToken cancellationToken = default)
    {
        Reference(source); ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested(); var snapshot = request.Snapshot();
        if (snapshot.Model != source.Model) throw new ArgumentException("remix 必须使用源任务的公共模型及完整目标。", nameof(request));
        var target = Route(source.Model).Resolve(source);
        if (target.Adapter is not IVideoRemixTaskAdapter adapter) throw new NotSupportedException("视频原目标不支持原生 remix。");
        return RemixCoreAsync(source, snapshot, target, adapter, cancellationToken);
    }
    /// <summary>派生结果必须是新任务，引用保留源任务的完整绑定，不改变源引用。</summary>
    private static async Task<VideoTask> RemixCoreAsync(VideoTaskReference source, VideoTaskRequest request, VideoTarget target, IVideoRemixTaskAdapter adapter, CancellationToken cancellationToken)
    {
        var result = await adapter.RemixAsync(source.TaskId, request, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("视频适配器未返回原生 remix 任务。");
        target.Validate(); cancellationToken.ThrowIfCancellationRequested();
        if (result.TaskId == source.TaskId) throw new InvalidDataException("remix 返回了源任务，未产生新的原生任务。");
        return new(source with { TaskId = result.TaskId }, result);
    }
    /// <summary>在原完整目标上显式读取一次原生内容，借用目标流；失败时可能已写入部分字节。</summary>
    public Task<VideoContentResult> CopyContentAsync(VideoTaskReference reference, Stream destination, VideoContentRequest? request = null, CancellationToken cancellationToken = default)
    {
        Reference(reference); ArgumentNullException.ThrowIfNull(destination); cancellationToken.ThrowIfCancellationRequested();
        if (!destination.CanWrite) throw new ArgumentException("原生视频内容目标流必须可写。", nameof(destination));
        var snapshot = (request ?? new VideoContentRequest()).Snapshot(); var target = Route(reference.Model).Resolve(reference);
        if (target.Adapter is not IVideoTaskContentAdapter adapter) throw new NotSupportedException("视频原目标不支持原生内容读取。");
        return CopyContentCoreAsync(reference, snapshot, destination, target, adapter, cancellationToken);
    }
    /// <summary>等待供应商最终资源释放与取消检查，验证返回元数据仍关联原任务。</summary>
    private static async Task<VideoContentResult> CopyContentCoreAsync(VideoTaskReference reference, VideoContentRequest request, Stream destination,
        VideoTarget target, IVideoTaskContentAdapter adapter, CancellationToken cancellationToken)
    {
        var result = await adapter.CopyContentAsync(reference.TaskId, request, destination, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("视频适配器未返回原生内容元数据。");
        target.Validate(); cancellationToken.ThrowIfCancellationRequested();
        if (result.TaskId != reference.TaskId) throw new InvalidDataException("视频内容元数据关联了其他任务。"); return result;
    }
    /// <summary>所有既有任务操作先校验引用文本，不允许损坏字符串改变目标或任务身份。</summary>
    private static void Reference(VideoTaskReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        foreach (var text in new[] { reference.Model, reference.TargetId, reference.Fingerprint, reference.TaskId })
        { ArgumentException.ThrowIfNullOrWhiteSpace(text); VideoJson.Text(text); }
    }
    /// <summary>目标和任务 ID 必须保持关联，不能把另一任务的终态公开给原引用。</summary>
    private static async Task<VideoTask> GetCoreAsync(VideoTaskReference reference, VideoTarget target, CancellationToken cancellationToken)
    {
        var result = await target.Adapter.GetAsync(reference.TaskId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("视频适配器未返回原生任务。");
        target.Validate(); cancellationToken.ThrowIfCancellationRequested();
        if (result.TaskId != reference.TaskId) throw new InvalidDataException("视频查询返回了其他任务的结果。");
        return new(reference, result);
    }
    /// <summary>只允许显式配置的公共路由，不推断供应商或模型。</summary>
    private VideoRouteState Route(string model) => _routes.TryGetValue(model, out var route) ? route : throw new InvalidOperationException("视频模型未配置。");
}
