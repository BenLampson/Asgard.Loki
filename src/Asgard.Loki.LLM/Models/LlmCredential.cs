namespace Asgard.Loki.LLM;

/// <summary>表示稳定标识的凭据，避免默认格式化泄漏密钥。</summary>
public sealed class LlmCredential
{
    /// <summary>创建凭据。</summary>
    public LlmCredential(string id, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        Id = id;
        ApiKey = apiKey;
    }

    /// <summary>凭据身份标识。</summary>
    public string Id { get; }
    /// <summary>仅供供应商认证使用的密钥。</summary>
    [JsonIgnore]
    public string ApiKey { get; }
    /// <inheritdoc />
    public override string ToString() => Id;
}
