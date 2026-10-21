namespace Asgard.Loki.LLM.AWS;

/// <summary>由应用实现的借用式签名凭据来源；登录、角色假设和刷新由应用负责。</summary>
public interface IAwsLlmCredentialSource
{
    /// <summary>稳定来源/账户/受众配置身份，不包含短期 Key；改变配置需要注册新供应商。</summary>
    string BindingIdentity { get; }
    /// <summary>仅为已绑定主体取得一次凭据；来源失败不重试生成或切换身份。</summary>
    ValueTask<AwsLlmSigningCredentials> GetCredentialsAsync(string principalIdentity, CancellationToken cancellationToken = default);
}
