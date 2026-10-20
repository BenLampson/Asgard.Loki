using System.Net;
using System.Text.Json;

namespace Asgard.Loki.LLM.Coze;

/// <summary>完整原生回执/状态/消息结果，不把请求受理等同于模型完成，不投影最后一条回答。</summary>
public sealed class CozeChatResult
{
    /// <summary>仅在全部拥有资源释放和最终取消检查后建立独立 JSON 所有权。</summary>
    internal CozeChatResult(string operation, Uri endpoint, HttpStatusCode status, string? contentType, JsonElement data, CozeChatMetadata metadata, CozeChatReference reference)
    { Operation = operation; Endpoint = endpoint; StatusCode = status; ContentType = contentType; Data = data.Clone(); Metadata = metadata; Reference = reference; }
    /// <summary>本次显式原生操作 create、retrieve 或 messages。</summary>
    public string Operation { get; }
    /// <summary>固定目标上的实际原生调用地址，不是从返回数据解析的 URL。</summary>
    public Uri Endpoint { get; }
    /// <summary>实际 HTTP 状态，不按原生 Chat 状态改写。</summary>
    public HttpStatusCode StatusCode { get; }
    /// <summary>上游原始媒体头，缺失时保持空值。</summary>
    public string? ContentType { get; }
    /// <summary>完整 code/msg/data/detail 和所有未知字段，保留消息数组原始顺序。</summary>
    public JsonElement Data { get; }
    /// <summary>诚实标记 Bot/服务器/凭据身份的独立元数据。</summary>
    public CozeChatMetadata Metadata { get; }
    /// <summary>显式后续查询使用的完整绑定引用，不触发轮询。</summary>
    public CozeChatReference Reference { get; }
    /// <summary>仅返回原生 Chat 状态；消息数组没有状态时保持 null。</summary>
    public string? Status => Data.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String ? status.GetString() : null;
    /// <summary>原生已知结束状态，包括失败和待工具输入，不表示模型或工具成功。</summary>
    public bool IsChatTerminal => Status is "completed" or "failed" or "canceled" or "requires_action";
}
