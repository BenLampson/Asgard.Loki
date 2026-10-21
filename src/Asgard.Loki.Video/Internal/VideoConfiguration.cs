namespace Asgard.Loki.Video.Internal;

/// <summary>注册时验证每个完整目标，包括备用目标；不访问网络或其他能力的状态。</summary>
internal sealed class VideoConfiguration
{
    /// <summary>保存不可被外部集合变化修改的路由与完整目标快照。</summary>
    internal VideoConfiguration(VideoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(options.Routes);
        foreach (var route in options.Routes)
        {
            ArgumentNullException.ThrowIfNull(route); ArgumentException.ThrowIfNullOrWhiteSpace(route.Model); VideoJson.Text(route.Model);
            ArgumentNullException.ThrowIfNull(route.Targets); ArgumentOutOfRangeException.ThrowIfZero(route.Targets.Count);
            var targets = route.Targets.Select(target => new VideoTarget(route.Model, target)).ToArray();
            if (targets.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != targets.Length) throw new ArgumentException("视频路由中的完整目标标识重复。");
            if (!Routes.TryAdd(route.Model, targets)) throw new ArgumentException("公共视频路由重复。");
        }
        ArgumentOutOfRangeException.ThrowIfZero(Routes.Count);
    }
    /// <summary>只由模块读取的路由快照，不包含客户端选择序号。</summary>
    internal Dictionary<string, VideoTarget[]> Routes { get; } = new(StringComparer.Ordinal);
}
