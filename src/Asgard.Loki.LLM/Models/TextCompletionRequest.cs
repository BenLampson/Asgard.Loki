namespace Asgard.Loki.LLM;

/// <summary>原生文本补全请求；prompt、token 批次和扩展参数保持供应商结构。</summary>
public sealed record TextCompletionRequest
{
    /// <summary>由调用者配置的公共模型路由名，不直接选择远端部署。</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }
    /// <summary>完整原生 prompt/suffix/echo/logprobs 等参数；保留省略、null、批次边界和未知字段。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
