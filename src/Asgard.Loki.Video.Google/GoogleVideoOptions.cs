using System.Text.Json.Serialization;

namespace Asgard.Loki.Video.Google;

/// <summary>Gemini Developer API 视频完整目标，不包含 Vertex 项目、令牌或自动凭据发现。</summary>
public sealed class GoogleVideoOptions
{
    /// <summary>完整上游目标标识。</summary>
    public string Id { get; init; } = "google-video";
    /// <summary>调用者明确提供的实际模型裸 ID，SDK 不指定默认模型。</summary>
    public required string Model { get; init; }
    /// <summary>目标 API Key，不参与配置 JSON 序列化。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>包含显式版本的原生服务根地址。</summary>
    public Uri BaseAddress { get; init; } = new("https://generativelanguage.googleapis.com/v1beta/");
    /// <summary>借用工厂的客户端名；该客户端必须禁用自动重定向和自动重试。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Video.Google";
    /// <summary>原生 JSON 提交字节上限，默认 64 MiB。</summary>
    public int MaximumRequestBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>原生 JSON 结果或错误字节上限，默认 16 MiB。</summary>
    public int MaximumResponseBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>一次显式内容传输的原始字节上限，默认 512 MiB。</summary>
    public long MaximumContentBytes { get; init; } = 512L * 1024 * 1024;
    /// <summary>仅内容 GET 允许的显式 HTTPS 重定向次数，默认三次；重定向不携带 API Key。</summary>
    public int MaximumContentRedirects { get; init; } = 3;
}
