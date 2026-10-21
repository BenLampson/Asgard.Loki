using Asgard.Loki.Image.Internal;

namespace Asgard.Loki.Image;

/// <summary>独立处理图像路由与公共调用。</summary>
public sealed class ImageClient
{
    private readonly Dictionary<string, IImageGenerationAdapter[]> _routes = new(StringComparer.Ordinal);
    private int _sequence = -1;
    private readonly ImageEditingClient _edits;
    /// <summary>校验并保存独立的图像配置快照。</summary>
    internal ImageClient(ImageConfiguration configuration)
    {
        _routes = configuration.GenerationRoutes.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        _edits = new ImageEditingClient(configuration);
    }
    /// <summary>生成完整图像结果。</summary>
    public Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Snapshot(request);
        return Select(snapshot.Model).GenerateAsync(snapshot, cancellationToken);
    }
    /// <summary>调用时复制参数并选择完整目标，枚举不再读取调用者原始参数。</summary>
    public IAsyncEnumerable<ImageGenerationEvent> StreamAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Snapshot(request);
        return Select(snapshot.Model).StreamAsync(snapshot, cancellationToken);
    }
    /// <summary>显式原生 JSON 图像编辑，未知字段/引用按原生结构传给已配置编辑目标。</summary>
    public Task<ImageGenerationResult> EditAsync(ImageEditRequest request, CancellationToken cancellationToken = default)
        => _edits.EditAsync(request, cancellationToken);
    /// <summary>显式 multipart 图像编辑，不自动下载、编码或回卷输入/遮罩流。</summary>
    public Task<ImageGenerationResult> EditMultipartAsync(ImageMultipartEditRequest request, CancellationToken cancellationToken = default)
        => _edits.EditMultipartAsync(request, cancellationToken);
    /// <summary>显式 JSON 编辑事件流；原生完成项须等待源正常结束和释放。</summary>
    public IAsyncEnumerable<ImageEditEvent> StreamEditAsync(ImageEditRequest request, CancellationToken cancellationToken = default)
        => _edits.StreamEditAsync(request, cancellationToken);
    /// <summary>显式 multipart 编辑事件流，整个配置路由必须支持原生流式。</summary>
    public IAsyncEnumerable<ImageEditEvent> StreamMultipartEditAsync(ImageMultipartEditRequest request, CancellationToken cancellationToken = default)
        => _edits.StreamMultipartEditAsync(request, cancellationToken);
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
        if (request.Parameters.ContainsKey("model") || request.Parameters.ContainsKey("prompt")
            || request.Parameters.Values.Any(value => value.ValueKind == System.Text.Json.JsonValueKind.Undefined))
            throw new ArgumentException("图像扩展参数不能覆盖公共字段或包含 Undefined。", nameof(request));
        return request with { Parameters = request.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal) };
    }
}
