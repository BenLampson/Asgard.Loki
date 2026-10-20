using System.Text.Json.Serialization;

namespace Asgard.Loki.Embedding.Azure;

/// <summary>Azure 原生部署式嵌入完整目标，不发现资源、迁移版本或使用其他能力凭据。</summary>
public sealed class AzureEmbeddingOptions
{
    /// <summary>固定目标的稳定标识。</summary>
    public string Id { get; init; } = "azure-embedding";
    /// <summary>明确的资源根地址，可以包含调用者的路径前缀。</summary>
    public required Uri Endpoint { get; init; }
    /// <summary>实际部署名称，保留字面点号，不当作公共模型别名。</summary>
    public required string Deployment { get; init; }
    /// <summary>明确的原生 API 版本，不提供默认版本或自动迁移。</summary>
    public required string ApiVersion { get; init; }
    /// <summary>独立 api-key，不进入默认配置 JSON。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>可选原生请求体 model 原文；null 表示不添加该字段，不由部署或路由推测。</summary>
    public string? BodyModel { get; init; }
    /// <summary>借用工厂的客户端名；应用须禁用重试、对冲及重定向。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Embedding.Azure";
    /// <summary>完整原生请求 UTF-8 字节上限，默认 32 MiB。</summary>
    public int MaxRequestBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>完整原生响应或错误字节上限，默认 64 MiB。</summary>
    public int MaxResponseBytes { get; init; } = 64 * 1024 * 1024;
}
