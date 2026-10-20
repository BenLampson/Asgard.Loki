using System.Text.Json.Serialization;

namespace Asgard.Loki.Video.Midjourney;

/// <summary>非官方 Midjourney 代理视频的完整目标，不提供默认代理地址或账号服务。</summary>
public sealed class MidjourneyVideoOptions
{
    /// <summary>完整目标的稳定标识。</summary>
    public string Id { get; init; } = "midjourney-video";
    /// <summary>明确的 HTTP/HTTPS 代理 API 根地址，包含 mj/ 或模式前缀，不含操作路径。</summary>
    public required Uri BaseAddress { get; init; }
    /// <summary>固定的原生 videoType，作为实际上游视频类型，不注入其他模型默认值。</summary>
    public required string VideoType { get; init; }
    /// <summary>原始 mj-api-secret；null 明确表示匿名，不进入默认配置 JSON。</summary>
    [JsonIgnore]
    public string? ApiSecret { get; init; }
    /// <summary>借用工厂中的客户端名，调用方需关闭重试、对冲和重定向。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Video.Midjourney";
    /// <summary>原生 JSON 请求 UTF-8 字节上限，默认 64 MiB。</summary>
    public int MaximumRequestBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>原始结果或错误正文字节上限，默认 16 MiB。</summary>
    public int MaximumResponseBytes { get; init; } = 16 * 1024 * 1024;
}
