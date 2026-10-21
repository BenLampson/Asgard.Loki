namespace Asgard.Loki.LLM.OpenAI;

/// <summary>显式原生 HTTP 凭据位置；不进行认证探测、刷新或降级。</summary>
public enum OpenAiHttpAuthentication
{
    /// <summary>选中 API Key 作为标准 Bearer 头。</summary>
    Bearer,
    /// <summary>仅接受显式 Anonymous 凭据，不发送认证。</summary>
    None,
    /// <summary>选中 API Key 写入明确命名的请求头。</summary>
    Header,
    /// <summary>选中 API Key 经一次转义后写入明确命名的查询参数。</summary>
    Query
}
