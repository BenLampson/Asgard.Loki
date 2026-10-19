using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asgard.Loki.Embedding;

/// <summary>公共路由名与供应商原生嵌入参数；不把文本、分词或多模态输入改成统一格式。</summary>
public sealed record EmbeddingRequest
{
    /// <summary>调用者配置的公共模型路由名，由目标适配器映射为实际模型。</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }
    /// <summary>原生 input/content、编码、维度、任务类型及未知字段；不能覆盖根 model。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
