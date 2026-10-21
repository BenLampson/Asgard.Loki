using System.Text.Json.Serialization;

namespace Asgard.Loki.Embedding.Google;

/// <summary>配置完整 Google Developer API 嵌入目标，与 LLM、OpenAI 和 Vertex AI 身份独立。</summary>
public sealed class GoogleEmbeddingOptions
{
    /// <summary>固定目标的稳定实例标识。</summary>
    public string Id { get; init; } = "google-embedding";
    /// <summary>不带 models/ 前缀的实际模型 ID，写入原生路径。</summary>
    public required string Model { get; init; }
    /// <summary>构造适配器时必须提供的 API Key，不参与 JSON 序列化。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>包含版本路径的 HTTP(S) API 根地址。</summary>
    public Uri BaseAddress { get; init; } = new("https://generativelanguage.googleapis.com/v1beta/");
    /// <summary>固定原生操作，同步批调用需显式选择。</summary>
    public GoogleEmbeddingOperation Operation { get; init; }
    /// <summary>调用者注册的 HTTP 客户端名称。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Embedding.Google";
}
