using System.Text.Json;

namespace Asgard.Loki.Image;

/// <summary>统一的图像生成结果，完整保留用量和扩展字段。</summary>
public sealed class ImageGenerationResult
{
    private readonly IReadOnlyList<GeneratedImage>? _nativeImages;
    /// <summary>保存完整结果快照。</summary>
    public ImageGenerationResult(JsonElement data, ImageResultMetadata metadata) : this(data, metadata, null) { }
    /// <summary>保存原生结果及供应商图像视图，不改写原始 JSON；显式空集合表示合法纯文本结果。</summary>
    public ImageGenerationResult(JsonElement data, ImageResultMetadata metadata, IReadOnlyList<GeneratedImage>? nativeImages)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new ArgumentException("图像结果必须是对象。", nameof(data));
        ArgumentNullException.ThrowIfNull(metadata);
        Data = data.Clone();
        Metadata = metadata;
        if (nativeImages is not null)
        {
            if (nativeImages.Any(item => item is null)) throw new ArgumentException("原生图像视图不能包含空项。", nameof(nativeImages));
            _nativeImages = Array.AsReadOnly(nativeImages.ToArray());
        }
    }
    /// <summary>未删减的原始结果。</summary>
    public JsonElement Data { get; }
    /// <summary>不包含密钥的模型与供应商身份。</summary>
    public ImageResultMetadata Metadata { get; }
    /// <summary>按原始顺序访问生成图像。</summary>
    public IReadOnlyList<GeneratedImage> Images => _nativeImages ?? (Data.TryGetProperty("data", out var images)
        && images.ValueKind == JsonValueKind.Array ? images.EnumerateArray().Select(item => new GeneratedImage(item)).ToArray() : []);
}
