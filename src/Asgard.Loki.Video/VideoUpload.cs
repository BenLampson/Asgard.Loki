using System.Text.Json.Serialization;

namespace Asgard.Loki.Video;

/// <summary>调用者拥有的原生视频素材上传，不读取长度、回卷、打开路径或取得流释放权。</summary>
public sealed record VideoUpload
{
    /// <summary>原生文件字段名，素材角色由调用者及供应商协议明确指定。</summary>
    public required string Name { get; init; }
    /// <summary>从当前位置消费的可读流；调用者应保持其有效且不并发操作。</summary>
    [JsonIgnore]
    public Stream Content { get; init; } = null!;
    /// <summary>原始文件名，仅作为 multipart 元数据，不作为本地路径打开。</summary>
    public required string FileName { get; init; }
    /// <summary>可选原生媒体类型，不按文件扩展名推测或转换。</summary>
    public string? ContentType { get; init; }
}
