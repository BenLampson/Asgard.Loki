using System.Text.Json;

namespace Asgard.Loki.Video.MiniMax;

/// <summary>成功 v1 视频任务的原生结果文件元数据，不管理或下载文件。</summary>
public sealed class MiniMaxVideoResultFile
{
    /// <summary>仅在供应商验证视频用途与文件 ID 后交付完整独立快照。</summary>
    internal MiniMaxVideoResultFile(string fileId, string downloadUrl, JsonElement data)
    { FileId = fileId; DownloadUrl = downloadUrl; Data = data.Clone(); }
    /// <summary>精确的十进制文件标识视图，不经过浮点数；Data 保留原始 JSON 类型。</summary>
    public string FileId { get; }
    /// <summary>原始签名下载地址，不改写、不刷新、不转发 API Key。</summary>
    public string DownloadUrl { get; }
    /// <summary>完整原生 file/base_resp 及未知成员，包括时间、大小、用途和文件名。</summary>
    public JsonElement Data { get; }
}
