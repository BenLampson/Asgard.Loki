namespace Asgard.Loki.Audio;

/// <summary>完整原生文件操作结果，保留 JSON/text/字幕等原始字节和媒体类型，不抽取通用 transcript。</summary>
public sealed class AudioFileResult
{
    /// <summary>复制响应字节；原生成功、错误、编码与业务结构由具体供应商校验。</summary>
    public AudioFileResult(ReadOnlyMemory<byte> body, string contentType, AudioResultMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType); ArgumentNullException.ThrowIfNull(metadata);
        Body = body.ToArray(); ContentType = contentType; Metadata = metadata;
    }
    /// <summary>完整原生响应正文；不会重排分段、删除未知字段或改写时间戳/用量。</summary>
    public ReadOnlyMemory<byte> Body { get; }
    /// <summary>上游实际内容类型，可包含 charset 等原生参数。</summary>
    public string ContentType { get; }
    /// <summary>本次执行的固定目标身份。</summary>
    public AudioResultMetadata Metadata { get; }
}
