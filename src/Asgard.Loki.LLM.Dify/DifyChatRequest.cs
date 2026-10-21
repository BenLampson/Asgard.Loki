using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asgard.Loki.LLM.Dify;

/// <summary>原生应用消息；显式用户、查询和输入，不扁平化历史或补默认用户。</summary>
public sealed record DifyChatRequest
{
    /// <summary>调用者定义的应用内用户，同一用户才能使用对应原生会话。</summary>
    [JsonPropertyName("user")]
    public required string User { get; init; }
    /// <summary>原始查询，允许服务定义的空查询，不改写思考标记。</summary>
    [JsonPropertyName("query")]
    public required string Query { get; init; }
    /// <summary>必需的原生输入对象，变量名、未知值、数值精度完整保留。</summary>
    [JsonPropertyName("inputs")]
    public required JsonElement Inputs { get; init; }
    /// <summary>原生可选参数，包括 conversation_id、files、workflow_id 和 auto_generate_name；方法只拥有 response_mode。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = new(StringComparer.Ordinal);
}
