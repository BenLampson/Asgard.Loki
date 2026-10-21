namespace Asgard.Loki.LLM;

/// <summary>单条原生 generate NDJSON 记录；不聚合正文、用量或补写上下文。</summary>
public sealed class OllamaGenerateEvent(JsonElement data, LlmResultMetadata metadata) : LlmJsonEvent(data, metadata, null, null, null)
{
    /// <summary>原生完成标志，错误和未来记录允许缺省。</summary>
    public bool? Done => Data.TryGetProperty("done", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    /// <summary>原生非 null 错误保持其原始数据。</summary>
    public bool IsError => Data.TryGetProperty("error", out var value) && value.ValueKind != JsonValueKind.Null;
    /// <summary>原生 done/error 终态，不能据此推断生成质量。</summary>
    public bool IsTerminal => Done == true || IsError;
}
