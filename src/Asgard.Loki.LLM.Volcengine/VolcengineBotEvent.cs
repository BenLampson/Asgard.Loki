using System.Text.Json;

namespace Asgard.Loki.LLM.Volcengine;

/// <summary>完整原生 Bot 数据及独立 SSE 身份，实际 DONE 不伪造为 JSON 或回答。</summary>
public sealed class VolcengineBotEvent
{
    /// <summary>供应商传输完成原生校验后建立不可变数据快照。</summary>
    internal VolcengineBotEvent(string data, string? name, string? id, long? retry, JsonElement? json, VolcengineBotMetadata metadata, bool terminal)
    { Data = data; EventName = name; EventId = id; RetryMilliseconds = retry; Json = json?.Clone(); Metadata = metadata; IsTerminal = terminal; }
    /// <summary>原始 SSE data 文本，包括实际 [DONE]，不重编码。</summary>
    public string Data { get; }
    /// <summary>原生 JSON 对象；实际非 JSON DONE 保持 null。</summary>
    public JsonElement? Json { get; }
    /// <summary>独立传输事件名，不覆盖原生业务字段。</summary>
    public string? EventName { get; }
    /// <summary>原始传输 ID，不等同于请求、工具、插件或引用 ID。</summary>
    public string? EventId { get; }
    /// <summary>原始重连提示，仅作数据，不触发重连。</summary>
    public long? RetryMilliseconds { get; }
    /// <summary>固定 Bot 服务身份，不声明远端实际模型的 Session 绑定。</summary>
    public VolcengineBotMetadata Metadata { get; }
    /// <summary>原生全局 DONE 或错误终态，在完整 EOF/资源关闭后才交付；不是模型成功标记。</summary>
    public bool IsTerminal { get; }
}
