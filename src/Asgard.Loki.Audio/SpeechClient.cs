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
        cancellationToken.ThrowIfCancellationRequested();
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
        await foreach (var chunk in ReadCompletedAsync(target.StreamAsync(snapshot, cancellationToken), cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }

    /// <inheritdoc />
    public async Task<NativeSpeechResult> GenerateNativeAsync(NativeSpeechRequest request, CancellationToken cancellationToken = default)
    {
        using var audio = new MemoryStream(); List<JsonElement> frames = [];
        await foreach (var chunk in StreamNativeAsync(request, cancellationToken).ConfigureAwait(false))
        {
            await audio.WriteAsync(chunk.Audio, cancellationToken).ConfigureAwait(false); frames.Add(chunk.Data);
        }
        return new(audio.ToArray(), frames.AsReadOnly());
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SpeechChunk> StreamNativeAsync(NativeSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model); ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.Parameters.Any(p => p.Key == "model" || p.Value.ValueKind == JsonValueKind.Undefined))
            throw new ArgumentException("原生语音扩展字段不能覆盖根 model 或包含 Undefined。");
        if (!_routes.TryGetValue(request.Model, out var targets)) throw new InvalidOperationException("音频模型未配置。");
        // 不按适配能力过滤目标，否则会隐式改变调用者配置的调度集合。
        if (targets.Any(t => t is not INativeSpeechAdapter)) throw new NotSupportedException("本语音路由的全部目标必须明确支持原生 JSON 调用。");
        var snapshot = request with { Parameters = request.Parameters.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal) };
        var target = (INativeSpeechAdapter)targets[(int)((uint)Interlocked.Increment(ref _sequence) % (uint)targets.Length)];
        await foreach (var chunk in ReadCompletedAsync(target.StreamNativeAsync(snapshot, cancellationToken), cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }

    /// <summary>暂存完成块，直到源枚举和释放正常结束；迟到帧、错误和取消不能提前发出成功终态。</summary>
    private static async IAsyncEnumerable<SpeechChunk> ReadCompletedAsync(IAsyncEnumerable<SpeechChunk> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        SpeechChunk? terminal = null;
        await foreach (var chunk in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (terminal is not null) throw new InvalidDataException("音频适配器在完成标记之后继续输出。");
            if (chunk.IsCompleted) terminal = chunk;
            else yield return chunk;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (terminal is null) throw new InvalidDataException("音频流没有明确的完成标记。");
        yield return terminal;
    }
}
