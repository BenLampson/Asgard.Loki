namespace Asgard.Loki.LLM;

/// <summary>保留 Responses 输入项、响应链及未知协议参数。</summary>
public sealed record ResponseRequest
{
    /// <summary>对外逻辑模型名。</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }
    /// <summary>原始字符串或输入项数组；可包含工具结果和不透明推理项。</summary>
    [JsonPropertyName("input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Input { get; init; }
    /// <summary>上游原始响应 ID，不由类库伪造或重写。</summary>
    [JsonPropertyName("previous_response_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousResponseId { get; init; }
    /// <summary>指令、工具、缓存及其他扩展参数。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
