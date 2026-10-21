using System.Text.Json.Serialization;

namespace Asgard.Loki.Image.Volcengine;

/// <summary>独立的火山方舟图像完整目标，不借用 LLM/视频状态、模型或凭据。</summary>
public sealed class VolcengineImageOptions
{
    /// <summary>完整目标在公共图像路由中的身份。</summary>
    public string Id { get; init; } = "volcengine-image";
    /// <summary>固定的原生模型/推理接入点，未提供默认模型。</summary>
    public required string Model { get; init; }
    /// <summary>原生 Bearer 凭据，不参与默认配置 JSON 序列化。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>包含 api/v3 的原生根地址，可使用显式应用前缀。</summary>
    public Uri BaseAddress { get; init; } = new("https://ark.cn-beijing.volces.com/api/v3/");
    /// <summary>借用工厂的名称，应用应关闭自动重试、对冲与重定向。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Image.Volcengine";
    /// <summary>完整 UTF-8 JSON 请求字节上限，默认 32 MiB。</summary>
    public int MaxRequestBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>完整响应/错误或整个原生 SSE 的字节上限，默认 64 MiB。</summary>
    public int MaxResponseBytes { get; init; } = 64 * 1024 * 1024;
}
