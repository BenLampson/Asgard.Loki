namespace Asgard.Loki.LLM;

/// <summary>星火原生请求；Model 仅为 SDK 路由信息，不成为 WebSocket 根 model 字段。</summary>
public sealed record SparkRequest
{
    /// <summary>公共模型路由名，选定实际目标后映射至原生 parameter.chat.domain。</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }
    /// <summary>原生 header/parameter/payload 与未知字段；app_id/domain 和流式模式由明确调用契约拥有。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
