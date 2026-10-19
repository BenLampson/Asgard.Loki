using System.Text.Json.Serialization;

namespace Asgard.Loki.Embedding.Jina;

/// <summary>配置完整 Jina 原生嵌入目标，不从模型名推断模态、编码或上限。</summary>
public sealed class JinaEmbeddingOptions
{
    /// <summary>固定供应商实例的稳定标识。</summary>
    public string Id { get; init; } = "jina-embedding";
    /// <summary>实际模型名称，其参数与模态支持由调用者和上游约定。</summary>
    public required string Model { get; init; }
    /// <summary>调用者提供的 Jina Key，不参与 JSON 序列化。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>包含版本路径的 HTTP(S) API 根地址。</summary>
    public Uri BaseAddress { get; init; } = new("https://api.jina.ai/v1/");
    /// <summary>外部工厂注册的 HTTP 客户端名称。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Embedding.Jina";
}
