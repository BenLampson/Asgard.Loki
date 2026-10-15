using System.Text.Json;

namespace Asgard.Loki.Image;

/// <summary>保留单张图像的 Base64、URL、修订提示词和其他供应商字段。</summary>
public sealed class GeneratedImage
{
    /// <summary>保存单张图像结果的独立快照。</summary>
    public GeneratedImage(JsonElement data) => Data = data.Clone();
    /// <summary>未删减的图像数据项。</summary>
    public JsonElement Data { get; }
    /// <summary>供应商返回的 Base64 内容；URL 模式可能为空。</summary>
    public string? Base64 => Data.TryGetProperty("b64_json", out var value) ? value.GetString() : null;
    /// <summary>供应商返回的 URL，不由类库自动下载。</summary>
    public string? Url => Data.TryGetProperty("url", out var value) ? value.GetString() : null;
    /// <summary>按需解码供应商返回的图像，不改变编码格式。</summary>
    public ReadOnlyMemory<byte> Decode()
    {
        if (Base64 is not { } encoded) throw new InvalidOperationException("当前图像没有 Base64 内容。");
        try { return Convert.FromBase64String(encoded); }
        catch (FormatException error) { throw new InvalidDataException("图像 Base64 无效。", error); }
    }
}
