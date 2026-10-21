using System.Text.Json.Serialization;

namespace Asgard.Loki.Image.OpenAI;

/// <summary>配置独立的 OpenAI 图像生成或编辑完整目标，操作适配器由调用者显式选择。</summary>
public sealed class OpenAiImageOptions
{
    /// <summary>供应商实例标识。</summary>
    public string Id { get; init; } = "openai-image";
    /// <summary>上游实际图像模型，不与公共路由名混用。</summary>
    public required string Model { get; init; }
    /// <summary>图像 API 的认证凭据。</summary>
    [JsonIgnore]
    public required string ApiKey { get; init; }
    /// <summary>包含版本路径的 API 基础地址。</summary>
    public Uri BaseAddress { get; init; } = new("https://api.openai.com/v1/");
    /// <summary>可选的 OpenAI 组织。</summary>
    public string? Organization { get; init; }
    /// <summary>可选的 OpenAI 项目。</summary>
    public string? Project { get; init; }
    /// <summary>外部 HTTP 工厂中注册的名称。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Image.OpenAI";
}
