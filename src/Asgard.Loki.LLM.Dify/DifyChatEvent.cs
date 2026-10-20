using System.Text.Json;

namespace Asgard.Loki.LLM.Dify;

/// <summary>完整原生 SSE 数据及独立传输身份；不拼接 Agent 的重复完整回答。</summary>
public sealed class DifyChatEvent
{
    /// <summary>供应商完成原生关联校验后创建不可变快照。</summary>
    internal DifyChatEvent(string data, string? name, string? id, long? retry, JsonElement json, DifyChatMetadata metadata, bool terminal)
    { Data = data; EventName = name; EventId = id; RetryMilliseconds = retry; Json = json.Clone(); Metadata = metadata; IsTerminal = terminal; }
    /// <summary>原始 SSE data 文本，不重写转义、思考标记、音频或工具内容。</summary>
    public string Data { get; }
    /// <summary>独立 SSE 事件名，原生生命周期由根 JSON event 选择。</summary>
    public string? EventName { get; }
    /// <summary>原始传输 ID，不等同于消息、任务、思考、文件或运行 ID。</summary>
    public string? EventId { get; }
    /// <summary>原始重连提示，仅作为数据，不触发重连。</summary>
    public long? RetryMilliseconds { get; }
    /// <summary>完整独立原生 JSON，包括未知字段及原生失败/暂停状态。</summary>
    public JsonElement Json { get; }
    /// <summary>固定应用服务元数据，不虚构底层模型。</summary>
    public DifyChatMetadata Metadata { get; }
    /// <summary>原生应用终态，包括失败/停止/暂停；完成 EOF/资源关闭后才交付，不等同于成功。</summary>
    public bool IsTerminal { get; }
}
