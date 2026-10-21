using System.Text.Json.Serialization;

namespace Asgard.Loki.Video.Volcengine;

/// <summary>配置独立完整的方舟视频上游目标，不借用 LLM、Audio 或 Image 的配置。</summary>
public sealed class VolcengineVideoOptions
{
    /// <summary>完整上游目标的稳定标识。</summary>
    public string Id { get; init; } = "volcengine-video";
    /// <summary>实际视频模型或推理接入点，由调用者明确指定。</summary>
    public required string Model { get; init; }
    /// <summary>方舟视频 API Key，不参与配置 JSON 序列化。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>包含 API 版本路径的 HTTP(S) 根地址。</summary>
    public Uri BaseAddress { get; init; } = new("https://ark.cn-beijing.volces.com/api/v3/");
    /// <summary>借用 HTTP 工厂中的客户端名称。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Video.Volcengine";
    /// <summary>完整提交 JSON 的 UTF-8 字节上限，默认 64 MiB。</summary>
    public int MaximumRequestBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>完整结果或错误正文的原始字节上限，默认 16 MiB。</summary>
    public int MaximumResponseBytes { get; init; } = 16 * 1024 * 1024;
}
