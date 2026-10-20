namespace Asgard.Loki.Audio.Internal;

/// <summary>注册时固定音频原生任务路由；不借用视频或 LLM 的流程。</summary>
internal sealed class AudioTaskConfiguration
{
    /// <summary>验证每个目标，并复制外部集合。</summary>
    internal AudioTaskConfiguration(IReadOnlyList<AudioTaskRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        foreach (var route in routes)
        {
            ArgumentNullException.ThrowIfNull(route); ArgumentException.ThrowIfNullOrWhiteSpace(route.Model); AudioTaskJson.Text(route.Model);
            ArgumentNullException.ThrowIfNull(route.Targets); ArgumentOutOfRangeException.ThrowIfZero(route.Targets.Count);
            var targets = route.Targets.Select(adapter => new AudioTaskTarget(route.Model, adapter)).ToArray();
            if (targets.Select(target => target.Id).Distinct(StringComparer.Ordinal).Count() != targets.Length)
                throw new ArgumentException("音频任务路由中的目标标识重复。");
            if (!Routes.TryAdd(route.Model, targets)) throw new ArgumentException("公共音频任务路由重复。");
        }
    }
    /// <summary>不可通过应用原始集合更改的配置快照。</summary>
    internal Dictionary<string, AudioTaskTarget[]> Routes { get; } = new(StringComparer.Ordinal);
}
