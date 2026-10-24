using System.Text.Json;

namespace Asgard.Loki.Image;

/// <summary>保留单张图像的 Base64、URL、修订提示词和其他供应商字段。</summary>
public sealed class GeneratedImage
{
    private readonly bool _native;
    private readonly string? _url;
    private readonly string? _base64;
    /// <summary>保存单张图像结果的独立快照。</summary>
    public GeneratedImage(JsonElement data) => Data = data.Clone();
    /// <summary>保留原生图像项及明确提取的载荷，不为原始 JSON 添加字段。</summary>
    public GeneratedImage(JsonElement data, string? url, string? base64)
    { Data = data.Clone(); _native = true; _url = url; _base64 = base64; }
    /// <summary>未删减的图像数据项。</summary>
    public JsonElement Data { get; }
    /// <summary>可选的原始二进制图片，自定义 Handler 可以直接提供，不需要先编码 Base64。</summary>
    public LokiImageContent? Content { get; init; }
    /// <summary>供应商明确返回的图片媒体类型，未知时为空。</summary>
    public string? ContentType { get; init; }
    /// <summary>供应商返回的 Base64 内容；URL 模式可能为空。</summary>
    public string? Base64 => _native ? _base64 : Data.TryGetProperty("b64_json", out var value) ? value.GetString() : null;
    /// <summary>供应商返回的 URL，不由类库自动下载。</summary>
    public string? Url => _native ? _url : Data.TryGetProperty("url", out var value) ? value.GetString() : null;
    /// <summary>按需解码供应商返回的图像，不改变编码格式。</summary>
    public ReadOnlyMemory<byte> Decode()
    {
        if (Base64 is not { } encoded) throw new InvalidOperationException("当前图像没有 Base64 内容。");
        try { return Convert.FromBase64String(encoded); }
        catch (FormatException error) { throw new InvalidDataException("图像 Base64 无效。", error); }
    }
}
