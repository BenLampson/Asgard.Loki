using System.Text.Json.Serialization;

namespace Asgard.Loki.Embedding.Ollama;

/// <summary>固定 Ollama 原生嵌入目标；本地无认证与显式 Bearer 目标分别配置。</summary>
public sealed class OllamaEmbeddingOptions
{
    /// <summary>完整目标的稳定实例标识。</summary>
    public string Id { get; init; } = "ollama-embedding";
    /// <summary>实际原生模型名，保留 namespace、tag 或 digest，不自动追加后缀。</summary>
    public required string Model { get; init; }
    /// <summary>包含原生 api 路径的 HTTP(S) 根地址；适配器只追加 embed。</summary>
    public Uri BaseAddress { get; init; } = new("http://localhost:11434/api/");
    /// <summary>可选的调用者提供 Bearer Key；null 表示不增加认证头，不读取环境或登录。</summary>
    [JsonIgnore]
    public string? ApiKey { get; init; }
    /// <summary>调用者注册的 HTTP 客户端名称。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Embedding.Ollama";
}
