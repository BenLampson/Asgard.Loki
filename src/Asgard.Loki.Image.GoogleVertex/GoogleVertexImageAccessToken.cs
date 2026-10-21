using System.Text.Json.Serialization;

namespace Asgard.Loki.Image.GoogleVertex;

/// <summary>不可变原始 Bearer 与主体、到期断言；默认 JSON 和显示不包含认证材料。</summary>
public sealed class GoogleVertexImageAccessToken
{
    /// <summary>来源保证令牌与主体断言一致；SDK 不解析 JWT、发现凭据或添加原始令牌前缀。</summary>
    public GoogleVertexImageAccessToken(string principalIdentity, string accessToken, DateTimeOffset expiresOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalIdentity); ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        Internal.VertexImageJson.Text(principalIdentity); Internal.VertexImageJson.Text(accessToken);
        if (accessToken.Any(c => c is < '!' or > '~')) throw new ArgumentException("访问令牌必须为无空白/控制字符的 ASCII 文本。", nameof(accessToken));
        PrincipalIdentity = principalIdentity; AccessToken = accessToken; ExpiresOn = expiresOn;
    }
    /// <summary>必须与目标绑定的稳定主体相同。</summary>
    [JsonIgnore]
    public string PrincipalIdentity { get; }
    /// <summary>本次原始令牌，不进入任务引用或默认 JSON。</summary>
    [JsonIgnore]
    public string AccessToken { get; }
    /// <summary>发送之前已过期时失败，不自动重取。</summary>
    public DateTimeOffset ExpiresOn { get; }
    /// <inheritdoc />
    public override string ToString() => nameof(GoogleVertexImageAccessToken);
}
