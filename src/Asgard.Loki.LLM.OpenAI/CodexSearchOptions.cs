namespace Asgard.Loki.LLM.OpenAI;

/// <summary>固定 Codex 原生辅助搜索服务，不创建模型路由、Session 或搜索执行循环。</summary>
public sealed record CodexSearchOptions
{
    /// <summary>显式后端/代理完整 API 根，只追加 alpha/search。</summary>
    public required Uri BaseAddress { get; init; }
    /// <summary>固定账户及明确可选原生头。</summary>
    public required CodexAccountOptions Account { get; init; }
    /// <summary>应用定义的服务目标标识。</summary>
    public string TargetId { get; init; } = "codex-search";
    /// <summary>调用者定义的配置版本，不代表接口公开稳定版本。</summary>
    public string Version { get; init; } = "1";
    /// <summary>借用命名 HTTP 工厂的客户端名称。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.LLM.OpenAI.CodexSearch";
    /// <summary>完整原生 JSON 请求 UTF-8 字节上限，默认一 MiB。</summary>
    public int MaximumRequestBytes { get; init; } = 1024 * 1024;
    /// <summary>完整原始成功/错误响应字节上限，默认三十二 MiB。</summary>
    public int MaximumResponseBytes { get; init; } = 32 * 1024 * 1024;
}
