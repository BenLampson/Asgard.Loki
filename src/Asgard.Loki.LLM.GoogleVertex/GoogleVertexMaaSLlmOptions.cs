namespace Asgard.Loki.LLM.GoogleVertex;

/// <summary>MaaS Chat 完整调用的本地传输策略，不承担云端项目或模型管理。</summary>
public sealed record GoogleVertexMaaSLlmOptions
{
    /// <summary>完整 JSON 或 HTTP 错误正文的原始总字节上限。</summary>
    public int MaximumBodyBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>显式 quota project 请求头；为空时禁止继承工厂默认项目身份。</summary>
    public string? QuotaProjectId { get; init; }
}
