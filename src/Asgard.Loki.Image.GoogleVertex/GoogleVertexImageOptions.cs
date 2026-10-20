using System.Text.Json.Serialization;

namespace Asgard.Loki.Image.GoogleVertex;

/// <summary>显式 Vertex 图片完整目标，认证方式由适配器构造入口决定，不自动发现凭据。</summary>
public sealed class GoogleVertexImageOptions
{
    /// <summary>完整目标的稳定身份。</summary>
    public string Id { get; init; } = "google-vertex-image";
    /// <summary>完整 projects/{project}/locations/{location}/publishers/google/models/{model}；仅 API Key 入口也接受 Express publishers/google/models/{model}。</summary>
    public required string ModelResource { get; init; }
    /// <summary>明确版本和区域/全局主机的 HTTPS 根地址，不从模型资源推断服务器。</summary>
    public required Uri BaseAddress { get; init; }
    /// <summary>仅令牌入口的稳定主体断言，API Key 入口必须省略。</summary>
    public string? PrincipalIdentity { get; init; }
    /// <summary>仅 API Key 入口的原生 x-goog-api-key；令牌入口必须为空。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>显式原生 x-goog-user-project，不从资源项目或主体推断。</summary>
    public string? QuotaProjectId { get; init; }
    /// <summary>借用 HTTP 工厂客户端名称，应用应关闭重试、对冲和重定向。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Image.GoogleVertex";
    /// <summary>完整原生请求的 UTF-8 字节上限。</summary>
    public int MaxRequestBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>完整 JSON 或整个原生 SSE 的原始字节上限。</summary>
    public int MaxResponseBytes { get; init; } = 64 * 1024 * 1024;
}
