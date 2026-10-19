namespace Asgard.Loki.LLM;

/// <summary>Converse 独立原生请求；所有正文参数保留 JSON 原形、缺省与显式 null。</summary>
public sealed record BedrockConverseRequest
{
    /// <summary>对外逻辑模型名；实际模型/ARN 由固定目标绑定提供。</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }
    /// <summary>原生 messages、工具、推理、格式、媒体或提示变量；不注入缺省值或 stream 开关。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
