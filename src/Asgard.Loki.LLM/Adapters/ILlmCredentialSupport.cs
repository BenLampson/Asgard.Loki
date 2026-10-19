namespace Asgard.Loki.LLM;

/// <summary>供应商显式声明支持的凭据种类；未实现此可选契约的已有供应商只接受 API Key。</summary>
public interface ILlmCredentialSupport
{
    /// <summary>仅判断原生认证契约，不访问网络或读取其他项目凭据；模块注册逐目标校验。</summary>
    bool SupportsCredential(LlmCredentialKind kind);
}
