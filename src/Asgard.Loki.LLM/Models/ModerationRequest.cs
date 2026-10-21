namespace Asgard.Loki.LLM;

/// <summary>原生 Moderations 请求，不将审核结果变成应用内容策略或改写输入。</summary>
public sealed record ModerationRequest
{
    /// <summary>由调用者配置的公共模型路由名。</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }
    /// <summary>原生 input 及全部扩展 JSON；文本批次与多模态内容组的边界保持原样。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
