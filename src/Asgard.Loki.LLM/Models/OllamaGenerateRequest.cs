namespace Asgard.Loki.LLM;

/// <summary>Ollama 原生 generate 请求；prompt/context 等保持显式原生字段，不注入聊天结构。</summary>
public sealed record OllamaGenerateRequest
{
    /// <summary>调用者配置的公共模型路由名。</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }
    /// <summary>原生 prompt、suffix、system、template、context、think、format 等；字段省略/null 均保留。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
