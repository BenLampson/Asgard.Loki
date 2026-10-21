namespace Asgard.Loki.LLM.Anthropic;

/// <summary>调用者明确选择的 Messages 认证位置，不按模型名、主机或 Key 内容猜测。</summary>
public enum AnthropicAuthentication
{
    /// <summary>使用请求级 x-api-key，默认行为。</summary>
    ApiKey = 0,
    /// <summary>使用请求级 Authorization: Bearer，令牌由调用者直接提供。</summary>
    Bearer = 1,
    /// <summary>只接受显式 Anonymous 凭据，不发送认证。</summary>
    None = 2,
    /// <summary>将选中 API Key 写入 Transport 明确命名的请求头。</summary>
    Header = 3,
    /// <summary>将选中 API Key 转义一次后写入 Transport 明确命名的查询参数。</summary>
    Query = 4
}
