namespace Asgard.Loki.LLM.NewAPI;

/// <summary>固定原生搜索的明确 HTTP 认证位置，不进行账户发现、刷新或降级。</summary>
public enum NewApiSearchAuthentication
{
    /// <summary>选中 API Key 使用 Authorization: Bearer，默认行为。</summary>
    Bearer,
    /// <summary>只接受显式 Anonymous，不发送认证。</summary>
    None,
    /// <summary>选中 API Key 写入明确命名的请求头。</summary>
    Header,
    /// <summary>选中 API Key 转义一次后写入明确命名的查询参数。</summary>
    Query
}
