namespace Asgard.Loki.LLM;

/// <summary>完整 Ollama 原生 Chat JSON，不生成其他协议的响应 ID、用量或输出项。</summary>
public sealed class OllamaChatResponse(JsonElement data, LlmResultMetadata metadata) : LlmJsonResult(data, metadata)
{
    /// <summary>原始 message 对象；字段省略与显式 null 仍可通过 Data 区分。</summary>
    public JsonElement? Message => Data.TryGetProperty("message", out var value) ? value : null;
    /// <summary>原生 done 标志；不将 stop、length、load 或 unload 原因改写为其他协议。</summary>
    public bool? Done => Data.TryGetProperty("done", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
}
