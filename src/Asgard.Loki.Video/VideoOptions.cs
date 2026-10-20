namespace Asgard.Loki.Video;

/// <summary>仅配置 Video 自身的供应商路由，不共用 LLM、Audio 或 Image 的状态。</summary>
public sealed class VideoOptions
{
    /// <summary>注册时校验并复制的原生视频路由集合。</summary>
    public required IReadOnlyList<VideoRoute> Routes { get; init; }
}
