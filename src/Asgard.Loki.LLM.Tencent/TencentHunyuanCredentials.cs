using System.Text.Json.Serialization;
using Asgard.Loki.LLM.Tencent.Internal;

namespace Asgard.Loki.LLM.Tencent;

/// <summary>不可变原生 SecretId/SecretKey 及可选临时令牌；默认显示和 JSON 不包含认证材料。</summary>
public sealed class TencentHunyuanCredentials
{
    /// <summary>逻辑主体独立于轮换的 SecretId；长期凭据也需明确到期断言。</summary>
    public TencentHunyuanCredentials(string principalIdentity, string secretId, string secretKey, DateTimeOffset expiresOn, string? token = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalIdentity); TencentJson.Text(principalIdentity);
        Check(secretId); Check(secretKey); if (token is not null) Check(token);
        if (secretId.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new ArgumentException("腾讯 SecretId 必须为 ASCII 字母或数字。");
        PrincipalIdentity = principalIdentity; SecretId = secretId; SecretKey = secretKey; ExpiresOn = expiresOn; Token = token;
    }
    /// <summary>必须与完整 Session 所绑定 External 主体完全一致。</summary>
    [JsonIgnore] public string PrincipalIdentity { get; }
    /// <summary>本次签名的原生 Key 标识，不进入默认序列化或缓存。</summary>
    [JsonIgnore] public string SecretId { get; }
    /// <summary>本次签名密钥，不记录或由 SDK 存储。</summary>
    [JsonIgnore] public string SecretKey { get; }
    /// <summary>显式临时凭据令牌，不填充、签发或自动刷新。</summary>
    [JsonIgnore] public string? Token { get; }
    /// <summary>发送及交付前验证到期；不触发另一次凭据获取。</summary>
    public DateTimeOffset ExpiresOn { get; }
    /// <summary>认证材料不能包含空白或控制字符，异常不回显输入。</summary>
    private static void Check(string value)
    { if (string.IsNullOrEmpty(value) || value.Any(c => c is < '!' or > '~')) throw new ArgumentException("腾讯签名凭据必须是无空白/控制字符的 ASCII 文本。"); }
    /// <inheritdoc />
    public override string ToString() => nameof(TencentHunyuanCredentials);
}
