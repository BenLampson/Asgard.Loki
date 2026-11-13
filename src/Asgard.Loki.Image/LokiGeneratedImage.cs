using System.Text.Json;

namespace Asgard.Loki.Image;

/// <summary>单张统一图片视图，保留原生条目；不因包含 URL 就推断整体生成成功。</summary>
public sealed class LokiGeneratedImage
{
    /// <summary>保存已有不可变原生图片视图。</summary>
    public LokiGeneratedImage(GeneratedImage nativeImage)
    { ArgumentNullException.ThrowIfNull(nativeImage); NativeImage = nativeImage; }
    /// <summary>原生图片视图。</summary>
    public GeneratedImage NativeImage { get; }
    /// <summary>原始图片条目。</summary>
    public JsonElement Data => NativeImage.Data;
    /// <summary>原生 URL。</summary>
    public string? Url => NativeImage.Url;
    /// <summary>原生 Base64。</summary>
    public string? Base64 => NativeImage.Base64;
    /// <summary>供应商明确返回的图片媒体类型。</summary>
    public string? ContentType => NativeImage.ContentType;
    /// <summary>原生图片级错误，不删除其他成功条目。</summary>
    public JsonElement? Error => Data.ValueKind == JsonValueKind.Object
        && Data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null ? error.Clone() : null;
}
