namespace Asgard.Loki.LLM.GoogleVertex;

/// <summary>Vertex 请求身份与本地资源策略，不管理云端项目或计费。</summary>
public sealed record GoogleVertexLlmOptions
{
    /// <summary>显式发送的 x-goog-user-project；不从主体或目标资源自动推断。</summary>
    public string? QuotaProjectId { get; init; }
    /// <summary>完整 JSON 或 HTTP 错误的原始字节上限；超限失败，不截断。</summary>
    public int MaximumBodyBytes { get; init; } = 32 * 1024 * 1024;
}
