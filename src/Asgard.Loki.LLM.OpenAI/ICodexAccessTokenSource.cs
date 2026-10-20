namespace Asgard.Loki.LLM.OpenAI;

/// <summary>由应用借给供应商的外部令牌来源；SDK 不读取认证文件、执行登录或刷新。</summary>
public interface ICodexAccessTokenSource
{
    /// <summary>稳定来源/受众/配置身份，不得含短期令牌；配置变化需要新供应商实例。</summary>
    string BindingIdentity { get; }
    /// <summary>为已绑定主体取得一次账户断言及原始令牌；失败或过期不隐式再次获取。</summary>
    ValueTask<CodexAccessToken> GetTokenAsync(string principalIdentity, CancellationToken cancellationToken = default);
}
