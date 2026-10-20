namespace Asgard.Loki.LLM;

/// <summary>完整 Cohere v1 Chat 原生响应，不投影为 Chat Completions 或合并工具结果。</summary>
public sealed class CohereChatCompletion(JsonElement data, LlmResultMetadata metadata) : LlmJsonResult(data, metadata)
{
    /// <summary>原生生成 ID；与 response_id 和继承的 id 分别保留，不建立 Responses 延续归属。</summary>
    public string? GenerationId => Data.TryGetProperty("generation_id", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    /// <summary>独立原生响应 ID，不能用生成 ID 补造。</summary>
    public string? ResponseId => Data.TryGetProperty("response_id", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    /// <summary>原生可选结束原因，未知值原样交付，不归一化为 stop。</summary>
    public string? FinishReason => Data.TryGetProperty("finish_reason", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
