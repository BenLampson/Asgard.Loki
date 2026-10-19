using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asgard.Loki.Audio;

/// <summary>公共语音路由名与供应商原生 JSON 参数，不改写 voice、输入或输出设置的类型。</summary>
public sealed record NativeSpeechRequest
{
    /// <summary>调用者配置的公共音频模型路由名。</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }
    /// <summary>供应商原生请求字段；根 model 保留给目标绑定，不得在扩展中覆盖。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
