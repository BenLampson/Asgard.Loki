using System.Text.Json.Serialization;
using Asgard.Loki.LLM.AWS.Internal;

namespace Asgard.Loki.LLM.AWS;

/// <summary>不可变 SigV4 凭据及主体/到期断言，默认 JSON 与显示不包含认证材料。</summary>
public sealed class AwsLlmSigningCredentials
{
    /// <summary>长期或临时访问 Key；临时凭据显式提供 Session Token，不解析或发现 AWS 身份。</summary>
    public AwsLlmSigningCredentials(string principalIdentity, string accessKeyId, string secretAccessKey, DateTimeOffset expiresOn, string? sessionToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalIdentity); AwsJson.Text(principalIdentity);
        Check(accessKeyId); Check(secretAccessKey); if (sessionToken is not null) Check(sessionToken);
        if (accessKeyId.Any(character => !char.IsAsciiLetterOrDigit(character))) throw new ArgumentException("AWS Access Key ID 必须是 ASCII 字母或数字。");
        PrincipalIdentity = principalIdentity; AccessKeyId = accessKeyId; SecretAccessKey = secretAccessKey; ExpiresOn = expiresOn; SessionToken = sessionToken;
    }
    /// <summary>必须与已绑定 External 主体完全一致。</summary>
    [JsonIgnore] public string PrincipalIdentity { get; }
    /// <summary>本次签名 Key ID，不进入缓存或默认 JSON。</summary>
    [JsonIgnore] public string AccessKeyId { get; }
    /// <summary>原始签名密钥，不记录、不缓存。</summary>
    [JsonIgnore] public string SecretAccessKey { get; }
    /// <summary>临时凭据的原始会话令牌；为空表示未选择临时令牌。</summary>
    [JsonIgnore] public string? SessionToken { get; }
    /// <summary>发送前到期立即失败，不隐式刷新。</summary>
    public DateTimeOffset ExpiresOn { get; }
    /// <summary>固定错误不回显原始密钥或令牌。</summary>
    private static void Check(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(character => character is < (char)33 or > (char)126)) throw new ArgumentException("AWS 签名凭据必须是无空白/控制字符的 ASCII 文本。");
    }
    /// <inheritdoc />
    public override string ToString() => nameof(AwsLlmSigningCredentials);
}
