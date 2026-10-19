using System.Text.Json.Serialization;

namespace Asgard.Loki.Embedding.OpenAI;

/// <summary>配置完整且独立的 OpenAI 嵌入目标，不读取 LLM 或图像供应商配置。</summary>
public sealed class OpenAiEmbeddingOptions
{
    /// <summary>稳定的供应商实例身份。</summary>
    public string Id { get; init; } = "openai-embedding";
    /// <summary>实际嵌入模型，与公共路由别名分开。</summary>
    public required string Model { get; init; }
    /// <summary>调用者提供的认证凭据，不参与 JSON 序列化。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>包含版本路径的 HTTP(S) 基础地址。</summary>
    public Uri BaseAddress { get; init; } = new("https://api.openai.com/v1/");
    /// <summary>可选组织标识。</summary>
    public string? Organization { get; init; }
    /// <summary>可选项目标识。</summary>
    public string? Project { get; init; }
    /// <summary>外部工厂中注册的 HTTP 客户端名称。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Embedding.OpenAI";
}
