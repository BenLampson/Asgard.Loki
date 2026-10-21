using System.Text.Json.Serialization;

namespace Asgard.Loki.LLM.OpenAI;

/// <summary>不可变的外部主体/账户断言与访问令牌，默认 JSON 和显示不回显凭据。</summary>
public sealed class CodexAccessToken
{
    /// <summary>提供原始不透明 Bearer 及到期时间，不解析 JWT 或获取账户配置。</summary>
    public CodexAccessToken(string principalIdentity, string accountId, string accessToken, DateTimeOffset expiresOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalIdentity); Internal.OpenAiHttpJson.Text(principalIdentity);
        Internal.CodexAuthentication.Header(accountId, false); Internal.CodexAuthentication.Header(accessToken, false);
        PrincipalIdentity = principalIdentity; AccountId = accountId; AccessToken = accessToken; ExpiresOn = expiresOn;
    }
    /// <summary>来源断言的稳定主体，必须与所选 External 凭据相同。</summary>
    [JsonIgnore]
    public string PrincipalIdentity { get; }
    /// <summary>来源断言的账户，必须与供应商固定账户相同。</summary>
    [JsonIgnore]
    public string AccountId { get; }
    /// <summary>原始 Bearer，不存入 Session、元数据、正文或默认格式化。</summary>
    [JsonIgnore]
    public string AccessToken { get; }
    /// <summary>调用时检查的到期时间，不触发自动刷新。</summary>
    public DateTimeOffset ExpiresOn { get; }
    /// <inheritdoc />
    public override string ToString() => nameof(CodexAccessToken);
}
