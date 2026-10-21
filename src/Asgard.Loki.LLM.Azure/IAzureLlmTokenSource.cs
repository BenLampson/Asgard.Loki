namespace Asgard.Loki.LLM.Azure;

/// <summary>应用实现并借给供应商的认证源，可以适配已有云 SDK；核心不保存或刷新令牌。</summary>
public interface IAzureLlmTokenSource
{
    /// <summary>稳定租户/受众/认证配置身份；不得包含短期令牌，配置改变应创建新供应商。</summary>
    string BindingIdentity { get; }
    /// <summary>按已绑定主体取得一次原始令牌；来源保证实际主体匹配声明，失败不触发模型重试。</summary>
    ValueTask<AzureLlmAccessToken> GetTokenAsync(string principalIdentity, CancellationToken cancellationToken = default);
}
