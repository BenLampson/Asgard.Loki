namespace Asgard.Loki.LLM.Anthropic;

/// <summary>调用者明确选择的 Messages 认证头，不按模型名、主机或 Key 内容猜测。</summary>
public enum AnthropicAuthentication
{
    /// <summary>使用请求级 x-api-key，默认行为。</summary>
    ApiKey = 0,
    /// <summary>使用请求级 Authorization: Bearer，令牌由调用者直接提供。</summary>
    Bearer = 1
}
