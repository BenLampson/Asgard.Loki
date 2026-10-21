using System.Text.Json.Serialization;
using Asgard.Loki.LLM.Xunfei.Internal;

namespace Asgard.Loki.LLM.Xunfei;

/// <summary>不可变星火 Key/Secret 和主体期限；默认 JSON/显示不包含认证材料。</summary>
public sealed class XunfeiSparkCredentials
{
    /// <summary>声明本次操作的稳定逻辑主体和期限，不解析三段式 Key 或从其他项目发现身份。</summary>
    public XunfeiSparkCredentials(string principalIdentity, string apiKey, string apiSecret, DateTimeOffset expiresOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalIdentity); SparkJson.Text(principalIdentity); Check(apiKey); Check(apiSecret);
        if (apiKey.Contains('"') || apiKey.Contains('\\')) throw new ArgumentException("星火 API Key 不能包含引号或转义符。");
        PrincipalIdentity = principalIdentity; ApiKey = apiKey; ApiSecret = apiSecret; ExpiresOn = expiresOn;
    }
    /// <summary>必须与已绑定 External 主体一致，不随短期 Key 轮换改变。</summary>
    [JsonIgnore] public string PrincipalIdentity { get; }
    /// <summary>原生 API Key，仅用于本次签名，不进入缓存或默认 JSON。</summary>
    [JsonIgnore] public string ApiKey { get; }
    /// <summary>原始 HMAC 密钥，不记录或由 SDK 持久化。</summary>
    [JsonIgnore] public string ApiSecret { get; }
    /// <summary>每个异步和交付边界验证期限；过期失败而非重新取得凭据。</summary>
    public DateTimeOffset ExpiresOn { get; }
    /// <summary>异常不回显认证材料。</summary>
    private static void Check(string value)
    { if (string.IsNullOrEmpty(value) || value.Any(c => c is < '!' or > '~')) throw new ArgumentException("星火认证材料必须是无空白/控制字符的 ASCII 文本。"); }
    /// <inheritdoc />
    public override string ToString() => nameof(XunfeiSparkCredentials);
}
