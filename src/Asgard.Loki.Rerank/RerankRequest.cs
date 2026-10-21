using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asgard.Loki.Rerank;

/// <summary>公共路由名与供应商原生重排参数，不统一改写查询、文档或多模态结构。</summary>
public sealed record RerankRequest
{
    /// <summary>调用者配置的公共模型路由名，由目标适配器映射为实际模型。</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }
    /// <summary>原生 query/documents、返回选项及未知字段；不能覆盖根 model。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
