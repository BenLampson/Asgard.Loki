namespace Asgard.Loki.LLM;

/// <summary>一条完整原生 NDJSON 记录；不虚构 SSE ID、事件名或重连字段。</summary>
public sealed class OllamaChatEvent(JsonElement data, LlmResultMetadata metadata) : LlmJsonEvent(data, metadata, null, null, null)
{
    /// <summary>原生 done，可缺省于错误或未来事件；缺省不等于模型完成。</summary>
    public bool? Done => Data.TryGetProperty("done", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    /// <summary>是否携带原生非 null 错误，完整错误数据保留在 Data。</summary>
    public bool IsError => Data.TryGetProperty("error", out var value) && value.ValueKind != JsonValueKind.Null;
    /// <summary>原生 done 或错误终态，不把协议结束等同于回答质量或工具执行成功。</summary>
    public bool IsTerminal => Done == true || IsError;
}
