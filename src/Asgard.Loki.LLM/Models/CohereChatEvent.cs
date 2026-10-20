namespace Asgard.Loki.LLM;

/// <summary>完整 Cohere v1 NDJSON 事件，保留原生工具分片、关联索引和未来字段。</summary>
public sealed class CohereChatEvent(JsonElement data, LlmResultMetadata metadata) : LlmJsonEvent(data, metadata, null, null, null)
{
    /// <summary>只读取原生 event_type，不将业务 type 字段或 SSE 身份当成流式生命周期。</summary>
    public override string? Type => Data.TryGetProperty("event_type", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    /// <summary>原生 stream-end 仅表示协议终态，不能等同于模型、工具或业务成功。</summary>
    public bool IsTerminal => Type == "stream-end";
    /// <summary>原生可选 finish_reason，不补全或翻译 ERROR、MAX_TOKENS 等值。</summary>
    public string? FinishReason => Data.TryGetProperty("finish_reason", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
