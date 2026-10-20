using System.Text.Json.Serialization;

namespace Asgard.Loki.Image.Midjourney;

/// <summary>明确配置的非官方图片代理完整目标，不提供代理地址、账户或模型默认值。</summary>
public sealed class MidjourneyImageOptions
{
    /// <summary>完整目标的稳定标识。</summary>
    public string Id { get; init; } = "midjourney-images";
    /// <summary>完整 HTTP(S) API 根地址，包括 mj/ 或调用者选择的速度模式前缀。</summary>
    public required Uri BaseAddress { get; init; }
    /// <summary>独立原始 mj-api-secret；null 明确表示匿名，不进入默认配置 JSON。</summary>
    [JsonIgnore]
    public string? ApiSecret { get; init; }
    /// <summary>借用工厂客户端名；应用须禁用自动重试、对冲和重定向。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Image.Midjourney";
    /// <summary>完整原生请求 UTF-8 字节上限，默认 64 MiB。</summary>
    public int MaxRequestBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>原始完整结果或错误字节上限，默认 16 MiB。</summary>
    public int MaxResponseBytes { get; init; } = 16 * 1024 * 1024;
}
