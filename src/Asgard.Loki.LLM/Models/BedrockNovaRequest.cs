namespace Asgard.Loki.LLM;

/// <summary>Nova Invoke 原生请求；正文版本、消息、工具、推理和未知字段由调用者明确提供。</summary>
public sealed record BedrockNovaRequest
{
    /// <summary>SDK 对外逻辑模型；实际模型 ID/ARN 属于完整目标绑定。</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }
    /// <summary>原生 JSON 参数；不补 schemaVersion、stream 或推理/媒体默认设置。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
