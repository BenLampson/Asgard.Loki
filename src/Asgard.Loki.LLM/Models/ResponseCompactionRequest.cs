namespace Asgard.Loki.LLM;

/// <summary>显式请求原生 Responses 压缩窗口，保留完整输入项及未知参数。</summary>
public sealed record ResponseCompactionRequest
{
    /// <summary>配置中的逻辑模型名；由目标映射为实际上游模型。</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }
    /// <summary>原始文本或完整输入项数组；不在客户端重建或裁剪历史。</summary>
    [JsonPropertyName("input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Input { get; init; }
    /// <summary>可选上游原始响应 ID，必须属于当前 Session。</summary>
    [JsonPropertyName("previous_response_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousResponseId { get; init; }
    /// <summary>指令、缓存控制及其他原生扩展字段。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
