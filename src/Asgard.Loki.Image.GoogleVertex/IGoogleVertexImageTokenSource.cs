namespace Asgard.Loki.Image.GoogleVertex;

/// <summary>由应用提供的借用认证源；真实登录、作用域、刷新和主体断言均由来源负责。</summary>
public interface IGoogleVertexImageTokenSource
{
    /// <summary>稳定的受众、主体配置或认证配置身份，不含短期令牌；配置变化需注册新目标。</summary>
    string BindingIdentity { get; }
    /// <summary>为当前绑定主体取得一次访问令牌；不隐式重试生成或切换主体。</summary>
    ValueTask<GoogleVertexImageAccessToken> GetTokenAsync(string principalIdentity, CancellationToken cancellationToken = default);
}
