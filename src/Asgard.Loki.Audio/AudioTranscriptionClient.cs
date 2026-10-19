using System.Runtime.CompilerServices;
using Asgard.Loki.Audio.Internal;

namespace Asgard.Loki.Audio;

/// <summary>Audio 自己的文件转录入口，独立路由、元数据快照及原生流终态，不运行应用工作流。</summary>
public sealed class AudioTranscriptionClient
{
    private readonly Dictionary<string, AudioFileRouteState<IAudioTranscriptionAdapter>> _routes;
    /// <summary>每个客户端使用自己的单路由选择状态，目标配置保持已复制的原始顺序。</summary>
    internal AudioTranscriptionClient(AudioFileConfiguration configuration)
        => _routes = configuration.TranscriptionRoutes.ToDictionary(p => p.Key, p => new AudioFileRouteState<IAudioTranscriptionAdapter>(p.Value), StringComparer.Ordinal);

    /// <summary>只选择并执行一次完整目标，不消费或取得上传流的所有权。</summary>
    public Task<AudioFileResult> CreateAsync(AudioFileRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = AudioFileRequestSnapshot.Create(request, cancellationToken);
        return Route(snapshot.Model).Select().TranscribeAsync(snapshot, cancellationToken);
    }

    /// <summary>仅在全体配置目标显式支持流式时派发，不过滤、降级或因失败重新上传。</summary>
    public IAsyncEnumerable<AudioTranscriptionEvent> StreamAsync(AudioFileRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = AudioFileRequestSnapshot.Create(request, cancellationToken); var route = Route(snapshot.Model);
        if (!route.All(target => target is IStreamingAudioTranscriptionAdapter)) throw new NotSupportedException("该转录路由的全部目标必须显式支持原生事件流。");
        var target = (IStreamingAudioTranscriptionAdapter)route.Select();
        return ReadCompletedAsync(target.StreamAsync(snapshot, cancellationToken), cancellationToken);
    }

    /// <summary>未知模型在任何目标选择前失败，不借用其他操作/能力的路由。</summary>
    private AudioFileRouteState<IAudioTranscriptionAdapter> Route(string model)
        => _routes.TryGetValue(model, out var route) ? route : throw new InvalidOperationException("文件转录模型未配置。");

    /// <summary>完成事件保持原始对象，等待源正常结束并释放，不能被后续数据或异常否定后仍报告成功。</summary>
    private static async IAsyncEnumerable<AudioTranscriptionEvent> ReadCompletedAsync(IAsyncEnumerable<AudioTranscriptionEvent> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        AudioTranscriptionEvent? terminal = null;
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is null) throw new InvalidDataException("转录适配器返回空事件。");
            if (terminal is not null) throw new InvalidDataException("转录完成后仍出现原生事件。");
            if (item.IsCompleted) terminal = item; else yield return item;
        }
        cancellationToken.ThrowIfCancellationRequested();
        yield return terminal ?? throw new InvalidDataException("转录流在原生完成事件之前结束。");
    }
}
