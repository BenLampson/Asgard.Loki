namespace Asgard.Loki.LLM;

/// <summary>混元原生 ChatCompletions 请求，消息、工具与扩展保持 PascalCase 原生结构。</summary>
public sealed record HunyuanRequest
{
    /// <summary>公共模型路由名；实际调用时只替换为已绑定目标的模型。</summary>
    [JsonPropertyName("Model")]
    public required string Model { get; init; }
    /// <summary>原生 Messages、Tools、推理与未知字段，不改写工具参数字符串或关联标识。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
