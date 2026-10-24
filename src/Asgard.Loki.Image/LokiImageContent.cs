namespace Asgard.Loki.Image;

/// <summary>显式获取的图片二进制内容，不自动保存本地文件或变换编码。</summary>
public sealed class LokiImageContent
{
    private readonly byte[] _bytes;
    /// <summary>复制完整图片内容，媒体类型未知时允许为空。</summary>
    public LokiImageContent(ReadOnlyMemory<byte> bytes, string? contentType)
    { _bytes = bytes.ToArray(); ContentType = contentType; }
    /// <summary>独立图片字节。</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes.ToArray();
    /// <summary>真实 HTTP 媒体类型或已识别的图片签名类型，不猜测缺失类型。</summary>
    public string? ContentType { get; }
}
