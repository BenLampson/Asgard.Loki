namespace Asgard.Loki.LLM;

/// <summary>声明 ClaudeMessages 原生请求，未知字段原样传递。</summary>
public sealed record ClaudeMessageRequest
{
    /// <summary>对外逻辑模型名称。</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }
    /// <summary>原生 messages 数组，保留工具、图片及不透明数据。</summary>
    [JsonPropertyName("messages")]
    public required JsonArray Messages { get; init; }
    /// <summary>调用方明确选择的最大输出 token 数。</summary>
    [JsonPropertyName("max_tokens")]
    public required int MaxTokens { get; init; }
    /// <summary>原生系统指令、工具、推理及其他供应商参数。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
