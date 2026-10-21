namespace Asgard.Loki.LLM.Xunfei;

/// <summary>应用拥有的星火凭据来源；账户发现、登录和刷新由应用负责。</summary>
public interface IXunfeiSparkCredentialSource
{
    /// <summary>稳定且无密钥的来源配置身份，变化时应注册新供应商。</summary>
    string BindingIdentity { get; }
    /// <summary>为已绑定 External 逻辑主体取得一次不可变凭据，不自动刷新或重试。</summary>
    ValueTask<XunfeiSparkCredentials> GetCredentialsAsync(string principalIdentity, CancellationToken cancellationToken = default);
}
