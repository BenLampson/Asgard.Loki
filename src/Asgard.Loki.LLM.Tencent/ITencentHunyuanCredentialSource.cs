namespace Asgard.Loki.LLM.Tencent;

/// <summary>应用拥有的混元签名凭据来源；登录、STS 获取和刷新留在调用应用。</summary>
public interface ITencentHunyuanCredentialSource
{
    /// <summary>稳定且无密钥的来源配置身份；配置改变需要注册新供应商。</summary>
    string BindingIdentity { get; }
    /// <summary>为已绑定逻辑主体取得一次凭据，不隐式重试、发现账户或切换主体。</summary>
    ValueTask<TencentHunyuanCredentials> GetCredentialsAsync(string principalIdentity, CancellationToken cancellationToken = default);
}
