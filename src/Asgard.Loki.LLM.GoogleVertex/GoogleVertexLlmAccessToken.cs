using System.Text.Json.Serialization;

namespace Asgard.Loki.LLM.GoogleVertex;

/// <summary>不可变原始 Bearer 与主体/到期断言；默认 JSON 和显示不包含认证材料。</summary>
public sealed class GoogleVertexLlmAccessToken
{
    /// <summary>不解析 JWT、不自动添加 Bearer 前缀；来源保证实际主体与断言一致。</summary>
    public GoogleVertexLlmAccessToken(string principalIdentity, string accessToken, DateTimeOffset expiresOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalIdentity); ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        Internal.VertexJson.Text(principalIdentity); Internal.VertexJson.Text(accessToken);
        if (accessToken.Any(char.IsWhiteSpace) || accessToken.Any(char.IsControl)) throw new ArgumentException("访问令牌不能包含空白或控制字符。", nameof(accessToken));
        PrincipalIdentity = principalIdentity; AccessToken = accessToken; ExpiresOn = expiresOn;
    }
    /// <summary>必须与已绑定凭据的稳定主体完全一致。</summary>
    [JsonIgnore]
    public string PrincipalIdentity { get; }
    /// <summary>本次原始令牌，不进入 Session 或默认 JSON。</summary>
    [JsonIgnore]
    public string AccessToken { get; }
    /// <summary>发送前过期即失败，不隐式刷新或重取。</summary>
    public DateTimeOffset ExpiresOn { get; }
    /// <inheritdoc />
    public override string ToString() => nameof(GoogleVertexLlmAccessToken);
}
