namespace Asgard.Loki.LLM;

/// <summary>保留 Ollama 原生消息与扩展参数，不转换 OpenAI 工具、推理或 Schema 外壳。</summary>
public sealed record OllamaChatRequest
{
    /// <summary>调用者配置的公共路由名。</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }
    /// <summary>原生 messages；工具关联、图片和 thinking 字段由调用者保留。</summary>
    [JsonPropertyName("messages")]
    public required JsonArray Messages { get; init; }
    /// <summary>原生 think、format、options、keep_alive 及未来字段；stream 由调用方法控制。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
