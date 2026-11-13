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
    /// <remarks>输出格式使用原生 response_format；透传不代表所选服务或模型支持。通过 LlmTarget.AllowedOutputFormats 按 ChatCompletions 声明允许的格式，SDK 调用前拒绝不支持的值，不自动降级。</remarks>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
