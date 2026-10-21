namespace Asgard.Loki.LLM.OpenAI;

/// <summary>调用者明确选择的 Codex 后端账户及可选原生头，不模拟 CLI 身份或执行登录。</summary>
public sealed record CodexAccountOptions
{
    /// <summary>固定 chatgpt-account-id，认证源必须断言同一账户。</summary>
    public required string AccountId { get; init; }
    /// <summary>显式 OpenAI-Beta 原生值；省略时不注入默认值或继承工厂头。</summary>
    public string? Beta { get; init; }
    /// <summary>显式 originator 原生值；省略时不猜测应用或 CLI 标识。</summary>
    public string? Originator { get; init; }
}
