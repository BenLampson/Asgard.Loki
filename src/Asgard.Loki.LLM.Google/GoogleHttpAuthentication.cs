namespace Asgard.Loki.LLM.Google;

/// <summary>明确的 Gemini HTTP 认证位置，不进行令牌发现、刷新或认证降级。</summary>
public enum GoogleHttpAuthentication
{
    /// <summary>选中 API Key 写入原生 x-goog-api-key，默认方式。</summary>
    ApiKey,
    /// <summary>选中 API Key 写入 Authorization: Bearer。</summary>
    Bearer,
    /// <summary>只接受显式 Anonymous，不发送认证。</summary>
    None,
    /// <summary>选中 API Key 写入明确命名的请求头。</summary>
    Header,
    /// <summary>选中 API Key 经一次转义后写入明确命名的查询参数。</summary>
    Query
}
