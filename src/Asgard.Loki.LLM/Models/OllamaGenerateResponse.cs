namespace Asgard.Loki.LLM;

/// <summary>完整原生 generate 结果，不转换聊天消息或把 context 当作服务器端会话 ID。</summary>
public sealed class OllamaGenerateResponse(JsonElement data, LlmResultMetadata metadata) : LlmJsonResult(data, metadata)
{
    /// <summary>原生 response 文本；完整结构与计数仍保留在 Data。</summary>
    public string? Response => Data.TryGetProperty("response", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    /// <summary>原生完成标志。</summary>
    public bool? Done => Data.TryGetProperty("done", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
}
