using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asgard.Loki.Image;

/// <summary>保留图像生成的公共字段和全部扩展协议参数。</summary>
public sealed record ImageGenerationRequest
{
    /// <summary>对外图像模型路由名。</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }
    /// <summary>原始图像生成提示词，不由类库改写。</summary>
    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }
    /// <summary>尺寸、质量、格式、透明背景等扩展字段。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
