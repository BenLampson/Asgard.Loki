using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Asgard.Loki.Audio;

/// <summary>独立处理语音路由、结果聚合及完成状态。</summary>
internal sealed class SpeechClient : ISpeechClient
{
    private readonly Dictionary<string, ISpeechAdapter[]> _routes = new(StringComparer.Ordinal);
    private int _sequence = -1;

    /// <summary>校验并快照化音频目标列表。</summary>
    internal SpeechClient(AudioOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.SpeechRoutes);
        foreach (var route in options.SpeechRoutes)
        {
            ArgumentNullException.ThrowIfNull(route);
            ArgumentException.ThrowIfNullOrWhiteSpace(route.Model);
            ArgumentNullException.ThrowIfNull(route.Targets);
            ArgumentOutOfRangeException.ThrowIfZero(route.Targets.Count);
            foreach (var target in route.Targets) ArgumentNullException.ThrowIfNull(target);
            if (!_routes.TryAdd(route.Model, route.Targets.ToArray())) throw new ArgumentException("音频路由重复。", nameof(options));
        }
        ArgumentOutOfRangeException.ThrowIfZero(_routes.Count);
    }

    /// <inheritdoc />
    public async Task<SpeechResult> GenerateAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        using var audio = new MemoryStream();
        List<JsonElement> frames = [];
        await foreach (var chunk in StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            await audio.WriteAsync(chunk.Audio, cancellationToken).ConfigureAwait(false);
            frames.Add(chunk.Data);
        }
        return new SpeechResult(audio.ToArray(), request.Format, frames.AsReadOnly());
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SpeechChunk> StreamAsync(SpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Voice);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Format);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        ArgumentNullException.ThrowIfNull(request.AudioParameters);
        if (request.SampleRate is { } sampleRate) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (!_routes.TryGetValue(request.Model, out var targets)) throw new InvalidOperationException("音频模型未配置。");
        var snapshot = request with { Parameters = request.Parameters.DeepClone().AsObject(), AudioParameters = request.AudioParameters.DeepClone().AsObject() };
        var target = targets[(int)((uint)Interlocked.Increment(ref _sequence) % (uint)targets.Length)];
        var completed = false;
        await foreach (var chunk in target.StreamAsync(snapshot, cancellationToken).ConfigureAwait(false))
        {
            if (completed) throw new InvalidDataException("音频适配器在完成标记之后继续输出。");
            completed = chunk.IsCompleted;
            yield return chunk;
        }
        if (!completed) throw new InvalidDataException("音频流没有明确的完成标记。");
    }
}
