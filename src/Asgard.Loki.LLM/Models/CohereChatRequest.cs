namespace Asgard.Loki.LLM;

/// <summary>原生 Cohere v1 Chat 请求；message、工具结果和扩展参数保持供应商结构。</summary>
public sealed record CohereChatRequest
{
    /// <summary>由调用者配置的公共模型路由名，不直接选择远端部署。</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }
    /// <summary>完整原生 message/chat_history/tools/tool_results 等参数；保留省略、null、批次边界和未知字段。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
