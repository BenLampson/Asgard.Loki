namespace Asgard.Loki.Video.Kling;

/// <summary>明确选择可灵原生鉴权，不通过凭据前缀或目标地址猜测认证类型。</summary>
public enum KlingVideoAuthentication
{
    /// <summary>显式原生 API Key，直接作为 Bearer 发送。</summary>
    ApiKey = 1,
    /// <summary>旧版接口的独立 AK/SK，每次请求生成原生 HS256 JWT。</summary>
    AccessKeyJwt = 2
}
