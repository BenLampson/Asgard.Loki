using System.Net;
using System.Text.Json;

namespace Asgard.Loki.LLM.Dify;

/// <summary>完整原生阻塞响应，保留独立事件/消息/任务/会话标识及全部元数据。</summary>
public sealed class DifyChatResult
{
    /// <summary>完成严格读取和全部拥有资源关闭后取得独立 JSON 所有权。</summary>
    internal DifyChatResult(Uri endpoint, HttpStatusCode status, string? contentType, JsonElement data, DifyChatMetadata metadata)
    { Endpoint = endpoint; StatusCode = status; ContentType = contentType; Data = data.Clone(); Metadata = metadata; }
    /// <summary>本次固定目标的实际原生地址。</summary>
    public Uri Endpoint { get; }
    /// <summary>实际 HTTP 状态，不按生成状态改写。</summary>
    public HttpStatusCode StatusCode { get; }
    /// <summary>原始媒体头，缺省时保持 null。</summary>
    public string? ContentType { get; }
    /// <summary>完整原生响应，不聚合用量或投影回答/思考/文件。</summary>
    public JsonElement Data { get; }
    /// <summary>原生应用身份，不冒充底层实际模型。</summary>
    public DifyChatMetadata Metadata { get; }
    /// <summary>原生暂停状态；暂停不等于成功，也不触发本地恢复循环。</summary>
    public bool IsPaused => Data.GetProperty("event").GetString() == "workflow_paused";
}
