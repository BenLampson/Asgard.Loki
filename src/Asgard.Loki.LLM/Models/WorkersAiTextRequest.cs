namespace Asgard.Loki.LLM;

/// <summary>Workers AI 原生文本请求；Model 只用于 SDK 路由，实际模型由已绑定目标的 URL 拥有。</summary>
public sealed record WorkersAiTextRequest
{
    /// <summary>公共模型路由名，不写入原生 ai/run 正文。</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }
    /// <summary>完整原生 prompt/messages、工具和生成参数，保留缺省、null、false、精确数值与未知字段。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
