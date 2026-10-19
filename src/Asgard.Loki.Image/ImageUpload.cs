using System.Text.Json.Serialization;

namespace Asgard.Loki.Image;

/// <summary>调用者持有的单次图像/遮罩上传，核心不读取长度、回卷、解码或取得释放权。</summary>
public sealed record ImageUpload
{
    /// <summary>必需可读流，派发前验证；从调用者当前位置读取，不参与 JSON 序列化。</summary>
    [JsonIgnore]
    public Stream Content { get; init; } = null!;
    /// <summary>原生上传文件名，不作为本地文件路径打开。</summary>
    public required string FileName { get; init; }
    /// <summary>可选文件媒体类型；不按扩展名猜测或自动转换。</summary>
    public string? ContentType { get; init; }
}
