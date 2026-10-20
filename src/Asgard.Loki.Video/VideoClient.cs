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
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model); VideoJson.Text(request.Model);
        ArgumentNullException.ThrowIfNull(request.Parameters); cancellationToken.ThrowIfCancellationRequested();
        if (request.Parameters.ContainsKey("model")) throw new ArgumentException("原生参数不能覆盖路由选择的实际模型。", nameof(request));
        var snapshot = request with { Parameters = VideoJson.Snapshot(request.Parameters) };
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
        ArgumentNullException.ThrowIfNull(reference);
        foreach (var text in new[] { reference.Model, reference.TargetId, reference.Fingerprint, reference.TaskId })
        { ArgumentException.ThrowIfNullOrWhiteSpace(text); VideoJson.Text(text); }
        cancellationToken.ThrowIfCancellationRequested();
        var target = Route(reference.Model).Resolve(reference);
        return GetCoreAsync(reference, target, cancellationToken);
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
