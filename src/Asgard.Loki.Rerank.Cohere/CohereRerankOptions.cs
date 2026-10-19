using System.Text.Json.Serialization;

namespace Asgard.Loki.Rerank.Cohere;

/// <summary>配置独立完整的 Cohere 重排目标，不引用 LLM、Embedding 或其他供应商配置。</summary>
public sealed class CohereRerankOptions
{
    /// <summary>稳定的供应商实例身份。</summary>
    public string Id { get; init; } = "cohere-rerank";
    /// <summary>实际上游模型，与公共路由别名分开。</summary>
    public required string Model { get; init; }
    /// <summary>固定的原生 API 版本；不自动降级或在失败后更换版本。</summary>
    public required CohereRerankApiVersion ApiVersion { get; init; }
    /// <summary>调用者提供的认证凭据，不参与 JSON 序列化。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>不含版本部分的 HTTP(S) 服务根地址，可包含调用者指定的租户路径。</summary>
    public Uri BaseAddress { get; init; } = new("https://api.cohere.com/");
    /// <summary>可选原生 X-Client-Name，不设置默认值。</summary>
    public string? ClientName { get; init; }
    /// <summary>外部工厂中注册的 HTTP 客户端名称。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Rerank.Cohere";
}
