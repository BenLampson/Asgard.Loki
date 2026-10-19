using Asgard.Loki.Audio.Internal;

namespace Asgard.Loki.Audio;

/// <summary>Audio 自己的文件翻译入口，不把 LLM 翻译或重新合成音频当成原生翻译。</summary>
public sealed class AudioTranslationClient
{
    private readonly Dictionary<string, AudioFileRouteState<IAudioTranslationAdapter>> _routes;
    /// <summary>为本客户端、本操作的各路由分别建立选择状态，不持有跨调用 Session。</summary>
    internal AudioTranslationClient(AudioFileConfiguration configuration)
        => _routes = configuration.TranslationRoutes.ToDictionary(p => p.Key, p => new AudioFileRouteState<IAudioTranslationAdapter>(p.Value), StringComparer.Ordinal);

    /// <summary>单次调用保留原生参数及完整字节结果，不自动回卷、重试或切换目标。</summary>
    public Task<AudioFileResult> CreateAsync(AudioFileRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = AudioFileRequestSnapshot.Create(request, cancellationToken);
        if (!_routes.TryGetValue(snapshot.Model, out var route)) throw new InvalidOperationException("文件翻译模型未配置。");
        return route.Select().TranslateAsync(snapshot, cancellationToken);
    }
}
