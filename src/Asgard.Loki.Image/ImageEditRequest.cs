using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asgard.Loki.Image;

/// <summary>原生 JSON 图像编辑请求；图片引用/遮罩等结构保留供应商字段，不转成上传或自动下载。</summary>
public sealed record ImageEditRequest
{
    /// <summary>公开图像编辑模型路由，由完整目标映射到实际模型。</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }
    /// <summary>原始编辑提示词，不做文本改写。</summary>
    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }
    /// <summary>原生图片引用、遮罩、输出设置及未知字段，不能覆盖公共 model/prompt。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
