using System.Text.Json.Serialization;

namespace Asgard.Loki.Audio.SunoAPI;

/// <summary>Suno-API 代理的固定原生目标，不内置公共代理地址或模型版本。</summary>
public sealed class SunoAudioOptions
{
    /// <summary>公共音频任务路由内唯一的完整目标标识。</summary>
    public string Id { get; init; } = "suno-audio";
    /// <summary>应用提供的 HTTPS 代理根地址，可包含前缀，不包含 suno/submit。</summary>
    public required Uri BaseAddress { get; init; }
    /// <summary>明确选择认证方式。</summary>
    public required SunoAudioAuthentication Authentication { get; init; }
    /// <summary>代理凭据，不是 Suno 账号 Cookie；不参与默认 JSON 序列化。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>音乐原生 mv；为空时仅允许歌词提交，不注入旧模型默认值。</summary>
    public string? MusicModel { get; init; }
    /// <summary>借用工厂中的客户端名；应用应禁用自动重试、对冲与重定向。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Audio.SunoAPI";
    /// <summary>原生 JSON 请求的字节上限，默认 4 MiB。</summary>
    public int MaximumRequestBytes { get; init; } = 4 * 1024 * 1024;
    /// <summary>完整原生响应或错误正文的字节上限，默认 16 MiB。</summary>
    public int MaximumResponseBytes { get; init; } = 16 * 1024 * 1024;
}
