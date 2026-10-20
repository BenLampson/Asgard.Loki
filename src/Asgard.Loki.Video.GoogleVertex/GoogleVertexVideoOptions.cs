namespace Asgard.Loki.Video.GoogleVertex;

/// <summary>Vertex Veo 的完整资源与调用目标，不管理云端项目、存储或计费。</summary>
public sealed class GoogleVertexVideoOptions
{
    /// <summary>完整目标的稳定标识。</summary>
    public string Id { get; init; } = "google-vertex-video";
    /// <summary>完整 projects/{project}/locations/{location}/publishers/google/models/{model} 原生资源名。</summary>
    public required string ModelResource { get; init; }
    /// <summary>调用者明确选择的稳定凭据主体，短期令牌轮换不改变该身份。</summary>
    public required string PrincipalIdentity { get; init; }
    /// <summary>包含显式版本的 HTTPS 根地址，例如区域主机下的 /v1/；不从资源名称推断主机。</summary>
    public required Uri BaseAddress { get; init; }
    /// <summary>原生 x-goog-user-project 请求头；省略时不从资源项目或凭据主体推断。</summary>
    public string? QuotaProjectId { get; init; }
    /// <summary>工厂中的命名客户端，须关闭自动重试和重定向。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Video.GoogleVertex";
    /// <summary>原生提交或查询正文的 UTF-8 字节上限，默认 64 MiB。</summary>
    public int MaximumRequestBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>完整原生结果或错误的字节上限，默认 128 MiB；包括返回的内联媒体字符串。</summary>
    public int MaximumResponseBytes { get; init; } = 128 * 1024 * 1024;
}
