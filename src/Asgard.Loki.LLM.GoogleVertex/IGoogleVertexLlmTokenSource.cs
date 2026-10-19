namespace Asgard.Loki.LLM.GoogleVertex;

/// <summary>由应用实现的借用认证源；真实登录、作用域、刷新和主体断言由来源负责。</summary>
public interface IGoogleVertexLlmTokenSource
{
    /// <summary>稳定受众/租户/认证配置身份，不含短期令牌；改变配置应注册新供应商。</summary>
    string BindingIdentity { get; }
    /// <summary>仅为本次已绑定主体取得一次令牌；失败不重试模型请求或切换主体。</summary>
    ValueTask<GoogleVertexLlmAccessToken> GetTokenAsync(string principalIdentity, CancellationToken cancellationToken = default);
}
