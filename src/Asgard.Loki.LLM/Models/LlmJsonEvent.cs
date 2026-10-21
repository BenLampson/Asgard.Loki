namespace Asgard.Loki.LLM;

/// <summary>保留完整流式协议事件及传输元数据。</summary>
public abstract class LlmJsonEvent : LlmJsonResult
{
    /// <summary>创建流式事件快照。</summary>
    protected LlmJsonEvent(JsonElement data, LlmResultMetadata metadata, string? eventName, string? eventId, long? retryMilliseconds)
        : base(data, metadata)
    {
        EventName = eventName;
        EventId = eventId;
        RetryMilliseconds = retryMilliseconds;
    }

    /// <summary>传输事件名；与 JSON 内的 type 分开保留。</summary>
    public string? EventName { get; }
    /// <summary>上游事件 ID，不作为生成请求重试依据。</summary>
    public string? EventId { get; }
    /// <summary>原始 SSE 重连提示，不触发自动重试生成。</summary>
    public long? RetryMilliseconds { get; }
    /// <summary>供应商协议事件类型。</summary>
    public virtual string? Type => Data.TryGetProperty("type", out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() : EventName;
}
