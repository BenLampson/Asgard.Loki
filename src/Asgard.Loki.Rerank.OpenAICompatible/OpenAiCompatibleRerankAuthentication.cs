namespace Asgard.Loki.Rerank.OpenAICompatible;

/// <summary>显式选择固定目标的原生认证方式，不自动发现凭据。</summary>
public enum OpenAiCompatibleRerankAuthentication
{
    /// <summary>无认证的调用者管理服务，不发送或借用工厂认证头。</summary>
    Anonymous,
    /// <summary>使用本目标显式提供的固定 Bearer 凭据。</summary>
    BearerToken
}
