using System.Runtime.CompilerServices;

namespace Asgard.Loki.Image;

/// <summary>独立处理图像路由与公共调用。</summary>
public sealed class ImageClient
{
    private readonly Dictionary<string, IImageGenerationAdapter[]> _routes = new(StringComparer.Ordinal);
    private int _sequence = -1;
    /// <summary>校验并保存独立的图像配置快照。</summary>
    internal ImageClient(ImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Routes);
        foreach (var route in options.Routes)
        {
            ArgumentNullException.ThrowIfNull(route);
            ArgumentException.ThrowIfNullOrWhiteSpace(route.Model);
            ArgumentNullException.ThrowIfNull(route.Targets);
            ArgumentOutOfRangeException.ThrowIfZero(route.Targets.Count);
            foreach (var target in route.Targets) ArgumentNullException.ThrowIfNull(target);
            if (!_routes.TryAdd(route.Model, route.Targets.ToArray())) throw new ArgumentException("图像模型路由重复。", nameof(options));
        }
        ArgumentOutOfRangeException.ThrowIfZero(_routes.Count);
    }
    /// <summary>生成完整图像结果。</summary>
    public Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = Snapshot(request);
        return Select(snapshot.Model).GenerateAsync(snapshot, cancellationToken);
    }
    /// <summary>读取完整图像生成事件。</summary>
    public async IAsyncEnumerable<ImageGenerationEvent> StreamAsync(ImageGenerationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var snapshot = Snapshot(request);
        await foreach (var item in Select(snapshot.Model).StreamAsync(snapshot, cancellationToken).ConfigureAwait(false)) yield return item;
    }
    /// <summary>只在当前图像路由内选择目标，不自动重试。</summary>
    private IImageGenerationAdapter Select(string model)
    {
        if (!_routes.TryGetValue(model, out var targets)) throw new InvalidOperationException("图像模型未配置。");
        return targets[(int)((uint)Interlocked.Increment(ref _sequence) % (uint)targets.Length)];
    }
    /// <summary>校验公共字段并复制扩展参数。</summary>
    private static ImageGenerationRequest Snapshot(ImageGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.Parameters.ContainsKey("model") || request.Parameters.ContainsKey("prompt"))
            throw new ArgumentException("图像扩展参数不能覆盖公共字段。", nameof(request));
        return request with { Parameters = request.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal) };
    }
}
