using System.Text.Json.Serialization;

namespace Asgard.Loki.Rerank.Jina;

/// <summary>配置独立完整的 Jina 重排目标；不复用其他供应商或能力的凭据。</summary>
public sealed class JinaRerankOptions
{
    /// <summary>稳定的供应商实例身份。</summary>
    public string Id { get; init; } = "jina-rerank";
    /// <summary>实际原生模型；其文本、多模态或 listwise 输入支持由调用者和上游约定。</summary>
    public required string Model { get; init; }
    /// <summary>调用者提供的认证凭据，不参与 JSON 序列化。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>包含原生版本路径的 HTTP(S) 基础地址。</summary>
    public Uri BaseAddress { get; init; } = new("https://api.jina.ai/v1/");
    /// <summary>外部工厂中注册的 HTTP 客户端名称。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Rerank.Jina";
}
