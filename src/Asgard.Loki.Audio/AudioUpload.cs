using System.Text.Json.Serialization;

namespace Asgard.Loki.Audio;

/// <summary>调用者持有的单次可读上传流；SDK 不预读、缓存、回卷或取得释放权。</summary>
public sealed record AudioUpload
{
    /// <summary>从当前读取位置发送的原始音频；调用结束前由调用者保持有效。</summary>
    [JsonIgnore]
    public Stream Content { get; init; } = null!;
    /// <summary>原生 multipart 文件名，不作为本地路径打开或写入。</summary>
    public required string FileName { get; init; }
    /// <summary>可选文件 part 媒体类型，省略时由供应商处理；核心不猜测编码。</summary>
    public string? ContentType { get; init; }
}
