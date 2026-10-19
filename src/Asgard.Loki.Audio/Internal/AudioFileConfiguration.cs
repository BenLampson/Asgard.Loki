namespace Asgard.Loki.Audio.Internal;

/// <summary>注册时复制文件操作的完整目标配置；构建后的客户端分别持有选择状态。</summary>
internal sealed class AudioFileConfiguration
{
    /// <summary>只处理 Audio 自己的原生转录和翻译配置，不取得外部目标的释放权。</summary>
    internal AudioFileConfiguration(AudioOptions options)
    {
        foreach (var route in options.TranscriptionRoutes)
        {
            ArgumentNullException.ThrowIfNull(route); ArgumentException.ThrowIfNullOrWhiteSpace(route.Model);
            ArgumentNullException.ThrowIfNull(route.Targets); var targets = route.Targets.ToArray(); ArgumentOutOfRangeException.ThrowIfZero(targets.Length);
            foreach (var target in targets) { ArgumentNullException.ThrowIfNull(target); ArgumentException.ThrowIfNullOrWhiteSpace(target.Id); }
            if (!TranscriptionRoutes.TryAdd(route.Model, targets)) throw new ArgumentException("转录模型路由重复。", nameof(options));
        }
        foreach (var route in options.TranslationRoutes)
        {
            ArgumentNullException.ThrowIfNull(route); ArgumentException.ThrowIfNullOrWhiteSpace(route.Model);
            ArgumentNullException.ThrowIfNull(route.Targets); var targets = route.Targets.ToArray(); ArgumentOutOfRangeException.ThrowIfZero(targets.Length);
            foreach (var target in targets) { ArgumentNullException.ThrowIfNull(target); ArgumentException.ThrowIfNullOrWhiteSpace(target.Id); }
            if (!TranslationRoutes.TryAdd(route.Model, targets)) throw new ArgumentException("翻译模型路由重复。", nameof(options));
        }
    }
    /// <summary>模块内部已复制的转录配置，不暴露给调用者。</summary>
    internal Dictionary<string, IAudioTranscriptionAdapter[]> TranscriptionRoutes { get; } = new(StringComparer.Ordinal);
    /// <summary>模块内部已复制的翻译配置，不暴露给调用者。</summary>
    internal Dictionary<string, IAudioTranslationAdapter[]> TranslationRoutes { get; } = new(StringComparer.Ordinal);
}
