using System.Text.Json;

namespace Asgard.Loki.LLM.Coze;

/// <summary>完整原生 SSE 帧；Chat、Message 与传输身份分层，未知数据保持原文。</summary>
public sealed class CozeChatEvent
{
    /// <summary>供应商完成原生校验后创建不可变快照，引用不包含凭据。</summary>
    internal CozeChatEvent(string data, string? name, string? id, long? retry, JsonElement? json, CozeChatMetadata metadata, CozeChatReference? reference, bool chatTerminal, bool terminal)
    { Data = data; EventName = name; EventId = id; RetryMilliseconds = retry; Json = json?.Clone(); Metadata = metadata; Reference = reference; IsChatTerminal = chatTerminal; IsTerminal = terminal; }
    /// <summary>原始 SSE data，包括不透明未来数据及带/不带 JSON 引号的实际 DONE；不重编码。</summary>
    public string Data { get; }
    /// <summary>原始 SSE 事件名，不从业务 JSON type 反推。</summary>
    public string? EventName { get; }
    /// <summary>原始传输 ID，与原生 Chat/Message/工具 ID 分别保留。</summary>
    public string? EventId { get; }
    /// <summary>原始重连提示，只作为数据，不触发重连或重试。</summary>
    public long? RetryMilliseconds { get; }
    /// <summary>已知 Chat/Message 及带引号 DONE 的独立原生 JSON；未知不透明数据和裸 DONE 保持 null。</summary>
    public JsonElement? Json { get; }
    /// <summary>固定服务/Bot 目标，不伪装为实际底层模型元数据。</summary>
    public CozeChatMetadata Metadata { get; }
    /// <summary>已知原生 Chat 身份的完整目标引用，早于身份建立的未来元数据保持 null。</summary>
    public CozeChatReference? Reference { get; }
    /// <summary>原生 completed/failed/requires_action Chat 事件；Message 完成不属于这一层，不表示模型或工具成功。</summary>
    public bool IsChatTerminal { get; }
    /// <summary>实际 native done 事件；供应商直到完整 EOF/资源释放/最终取消后才交付。</summary>
    public bool IsTerminal { get; }
}
