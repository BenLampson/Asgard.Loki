namespace Asgard.Loki.LLM;

/// <summary>保留 Gemini 完整原生 JSON 结果。</summary>
public sealed class GeminiContent(JsonElement data, LlmResultMetadata metadata) : LlmJsonResult(data, metadata)
{
    /// <summary>Gemini 原生响应身份，不伪造 OpenAI 的 id 字段。</summary>
    public string? ResponseId => Data.TryGetProperty("responseId", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
