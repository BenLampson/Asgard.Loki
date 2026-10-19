namespace Asgard.Loki.LLM;

/// <summary>表示稳定标识的凭据，避免默认格式化泄漏密钥。</summary>
public sealed class LlmCredential
{
    /// <summary>创建凭据。</summary>
    public LlmCredential(string id, string apiKey) : this(id, apiKey, LlmCredentialKind.ApiKey) { }

    /// <summary>认证方式只由显式构造入口决定，不把遗漏 Key 自动变成无认证。</summary>
    private LlmCredential(string id, string apiKey, LlmCredentialKind kind, string? principalIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (kind == LlmCredentialKind.ApiKey) ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (kind == LlmCredentialKind.External) ArgumentException.ThrowIfNullOrWhiteSpace(principalIdentity);
        Id = id;
        Kind = kind;
        ApiKey = apiKey;
        PrincipalIdentity = principalIdentity ?? "";
    }

    /// <summary>创建显式无认证凭据；不借用 Key，供应商必须明确支持此方式。</summary>
    public static LlmCredential Anonymous(string id) => new(id, "", LlmCredentialKind.Anonymous);
    /// <summary>声明稳定外部主体身份；实际令牌/签名材料由供应商借用调用者认证源取得，不存入核心。</summary>
    public static LlmCredential External(string id, string principalIdentity) => new(id, "", LlmCredentialKind.External, principalIdentity);

    /// <summary>凭据身份标识。</summary>
    public string Id { get; }
    /// <summary>明确认证种类；默认 API Key 省略序列化，保留既有元数据外壳。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public LlmCredentialKind Kind { get; }
    /// <summary>仅 API Key 凭据可读取密钥；其他种类明确失败，防止旧供应商误发空 Key。</summary>
    [JsonIgnore]
    public string ApiKey { get => Kind == LlmCredentialKind.ApiKey ? field : throw new InvalidOperationException("此凭据不包含 API Key。"); }
    /// <summary>仅外部认证可读取稳定主体引用，不接受或持有短期访问令牌；默认格式化和 JSON 不回显该值。</summary>
    [JsonIgnore]
    public string PrincipalIdentity { get => Kind == LlmCredentialKind.External ? field : throw new InvalidOperationException("此凭据不包含外部主体引用。"); }
    /// <inheritdoc />
    public override string ToString() => Id;
}
