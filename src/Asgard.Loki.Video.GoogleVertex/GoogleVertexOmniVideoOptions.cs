namespace Asgard.Loki.Video.GoogleVertex;

/// <summary>Gemini Omni 视频的完整 Interactions 目标，不推断部署区域或实际模型。</summary>
public sealed class GoogleVertexOmniVideoOptions
{
    /// <summary>完整目标的稳定标识。</summary>
    public string Id { get; init; } = "google-vertex-omni-video";
    /// <summary>完整 HTTPS 集合地址，包含版本和 projects/{project}/locations/{location}/interactions。</summary>
    public required Uri InteractionsEndpoint { get; init; }
    /// <summary>写入原生 model 字段的实际 Gemini Omni 模型。</summary>
    public required string Model { get; init; }
    /// <summary>调用者声明的稳定凭据主体，不随访问令牌轮换而改变。</summary>
    public required string PrincipalIdentity { get; init; }
    /// <summary>显式原生 x-goog-user-project 请求头；省略时不推断。</summary>
    public string? QuotaProjectId { get; init; }
    /// <summary>借用工厂中的客户端名；客户端须关闭自动重试和重定向。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Video.GoogleVertex.Omni";
    /// <summary>原生提交正文的 UTF-8 字节上限，默认 64 MiB。</summary>
    public int MaximumRequestBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>单次完整响应或错误的字节上限，默认 128 MiB，包括内联视频。</summary>
    public int MaximumResponseBytes { get; init; } = 128 * 1024 * 1024;
}
