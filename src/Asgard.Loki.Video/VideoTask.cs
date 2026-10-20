namespace Asgard.Loki.Video;

/// <summary>将原生视频任务窗口与其不可自动切换的完整目标引用组合。</summary>
public sealed class VideoTask
{
    /// <summary>仅在能力模块验证目标和任务 ID 后创建公共调用结果。</summary>
    internal VideoTask(VideoTaskReference reference, VideoTaskSnapshot snapshot) { Reference = reference; Snapshot = snapshot; }
    /// <summary>用于后续显式查询及应用持久化的原始引用。</summary>
    public VideoTaskReference Reference { get; }
    /// <summary>本次调用获取的原生状态和结果；不会启动自动查询或下载。</summary>
    public VideoTaskSnapshot Snapshot { get; }
}
