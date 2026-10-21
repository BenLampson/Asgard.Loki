using System.Text.Json.Serialization;

namespace Asgard.Loki.Audio.Cloudflare;

/// <summary>固定 Cloudflare 账户、实际模型、身份及原生输入结构。</summary>
public sealed class CloudflareAudioFileOptions
{
    /// <summary>完整供应商适配实例身份。</summary>
    public string Id { get; init; } = "cloudflare-audio";
    /// <summary>原生账户路径标识，不读取本机登录状态。</summary>
    public required string AccountId { get; init; }
    /// <summary>实际原生模型路径，公共请求 Model 仅用于路由。</summary>
    public required string Model { get; init; }
    /// <summary>必须显式选择输入结构；模型须支持相应原生 Schema。</summary>
    public required CloudflareAudioInputFormat InputFormat { get; init; }
    /// <summary>固定账户访问令牌，不借用其他能力的凭据。</summary>
    [JsonIgnore]
    public string ApiToken { get; init; } = "";
    /// <summary>完整 API 根地址，不包括 accounts 和实际模型路径。</summary>
    public Uri BaseAddress { get; init; } = new("https://api.cloudflare.com/client/v4/");
    /// <summary>借用的命名 HTTP 客户端。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Audio.Cloudflare";
    /// <summary>当前位置之后的原始上传字节上限，JSON Base64 也按原始音频计数。</summary>
    public long MaxUploadBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>不含音频的原生 JSON 参数字节上限。</summary>
    public int MaxParameterBytes { get; init; } = 64 * 1024;
    /// <summary>完整原生响应 JSON 字节上限。</summary>
    public int MaxResponseBytes { get; init; } = 16 * 1024 * 1024;
}
