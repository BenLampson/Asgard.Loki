namespace Asgard.Loki.LLM;

/// <summary>保留完整协议 JSON 与独立路由元数据。</summary>
public abstract class LlmJsonResult
{
    /// <summary>复制 JSON 所有权，避免原始文档释放影响结果。</summary>
    protected LlmJsonResult(JsonElement data, LlmResultMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (data.ValueKind != JsonValueKind.Object) throw new ArgumentException("协议结果必须是 JSON 对象。", nameof(data));
        Data = data.Clone();
        Metadata = metadata;
    }

    /// <summary>未删减的供应商协议结果。</summary>
    public JsonElement Data { get; }
    /// <summary>调用的路由信息。</summary>
    public LlmResultMetadata Metadata { get; }
    /// <summary>协议结果中的原始 ID。</summary>
    public string? Id => Data.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() : null;
}
