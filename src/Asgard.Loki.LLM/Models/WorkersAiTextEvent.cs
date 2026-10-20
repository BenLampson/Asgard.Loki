using Asgard.Loki.LLM.Internal;

namespace Asgard.Loki.LLM;

/// <summary>原生文本 SSE 帧，保留完整数据与实际 DONE 控制帧，不伪造 JSON 终态。</summary>
public sealed class WorkersAiTextEvent
{
    /// <summary>复制原生 JSON 所有权；只有完整字面 [DONE] 没有 JSON 对象。</summary>
    public WorkersAiTextEvent(string data, LlmResultMetadata metadata, string? eventName = null, string? eventId = null, long? retryMilliseconds = null)
    {
        ArgumentNullException.ThrowIfNull(data); ArgumentNullException.ThrowIfNull(metadata);
        LlmJsonInput.Text(data); LlmJsonInput.Text(eventName); LlmJsonInput.Text(eventId);
        if (data != "[DONE]")
        {
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Workers AI 文本 SSE 数据必须是原生 JSON 对象或实际 [DONE]。");
            LlmJsonInput.Request(null, null, new() { ["event"] = document.RootElement }); Json = document.RootElement.Clone();
        }
        Data = data; Metadata = metadata; EventName = eventName; EventId = eventId; RetryMilliseconds = retryMilliseconds;
    }
    /// <summary>完整 SSE data 字符串，包含原生 [DONE] 或原样 JSON，不归一化生成内容。</summary>
    public string Data { get; }
    /// <summary>独立不可变的原生 JSON 对象；实际 [DONE] 帧时为空，不合成占位对象。</summary>
    public JsonElement? Json { get; }
    /// <summary>完整目标路由元数据，与原生正文分开保留。</summary>
    public LlmResultMetadata Metadata { get; }
    /// <summary>原始 SSE 事件名，不能决定模型终态。</summary>
    public string? EventName { get; }
    /// <summary>原始 SSE 事件 ID，不触发重试或响应延续。</summary>
    public string? EventId { get; }
    /// <summary>原始 SSE 重连提示，只作为数据，不自动重连。</summary>
    public long? RetryMilliseconds { get; }
    /// <summary>只表示实际 [DONE] 控制帧；供应商必须在 EOF、资源释放和最终取消检查后交付。</summary>
    public bool IsTerminal => Data == "[DONE]";
}
