using System.Text.Json.Serialization;

namespace Asgard.Loki.LLM.Azure;

/// <summary>认证源返回的不可变令牌与主体断言，默认 JSON/显示不包含敏感材料。</summary>
public sealed class AzureLlmAccessToken
{
    /// <summary>提供原始 Bearer 内容和到期时间，不解析或猜测 JWT 声明。</summary>
    public AzureLlmAccessToken(string principalIdentity, string accessToken, DateTimeOffset expiresOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalIdentity); ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        Internal.AzureJson.Text(principalIdentity); Internal.AzureJson.Text(accessToken);
        if (accessToken.Any(char.IsWhiteSpace) || accessToken.Any(char.IsControl)) throw new ArgumentException("访问令牌不能包含空白或控制字符。", nameof(accessToken));
        PrincipalIdentity = principalIdentity; AccessToken = accessToken; ExpiresOn = expiresOn;
    }
    /// <summary>来源报告的稳定主体，必须与本次凭据相同。</summary>
    [JsonIgnore]
    public string PrincipalIdentity { get; }
    /// <summary>原始访问令牌，不进入 Session、正文或默认格式化。</summary>
    [JsonIgnore]
    public string AccessToken { get; }
    /// <summary>到期时间；过期时失败，不隐式再次取得令牌。</summary>
    public DateTimeOffset ExpiresOn { get; }
    /// <inheritdoc />
    public override string ToString() => nameof(AzureLlmAccessToken);
}
