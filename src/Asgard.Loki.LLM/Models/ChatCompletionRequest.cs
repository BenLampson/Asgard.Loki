namespace Asgard.Loki.LLM;

/// <summary>保留 Chat Completions 消息结构及未知协议参数。</summary>
public sealed record ChatCompletionRequest
{
    /// <summary>对外逻辑模型名。</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }
    /// <summary>原始消息数组，保留工具与多模态内容。</summary>
    [JsonPropertyName("messages")]
    public required JsonArray Messages { get; init; }
    /// <summary>未在本类型声明的所有协议字段。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
