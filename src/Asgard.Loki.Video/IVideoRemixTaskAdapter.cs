namespace Asgard.Loki.Video;

/// <summary>可选的原生 remix 派生任务能力，源任务必须保持其完整目标绑定。</summary>
public interface IVideoRemixTaskAdapter : IVideoTaskAdapter
{
    /// <summary>针对同一完整上游的原始任务提交一次原生 remix，返回新任务及完整原文。</summary>
    Task<VideoTaskSnapshot> RemixAsync(string sourceTaskId, VideoTaskRequest request, CancellationToken cancellationToken = default);
}
