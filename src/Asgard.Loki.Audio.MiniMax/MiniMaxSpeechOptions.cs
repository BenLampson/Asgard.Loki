using System.Text.Json.Serialization;

namespace Asgard.Loki.Audio.MiniMax;

/// <summary>固定 MiniMax 原生语音模型及 HTTP 目标，不推断账号或地域。</summary>
public sealed class MiniMaxSpeechOptions
{
    /// <summary>适配实例标识。</summary>
    public string Id { get; init; } = "minimax-speech";
    /// <summary>实际调用的原生模型，不是公共路由名。</summary>
    public required string Model { get; init; }
    /// <summary>独立语音访问凭据。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>完整原生接口地址，国际或其他地域需显式配置。</summary>
    public Uri Endpoint { get; init; } = new("https://api.minimax.cn/v1/t2a_v2");
    /// <summary>借用 HTTP 工厂的命名客户端。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Audio.MiniMax";
    /// <summary>完整请求 JSON 最大字节数。</summary>
    public int MaxRequestBytes { get; init; } = 1024 * 1024;
    /// <summary>完整响应 JSON 最大字节数，含 hex 音频。</summary>
    public int MaxResponseBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>hex 解码后的音频最大字节数。</summary>
    public int MaxAudioBytes { get; init; } = 16 * 1024 * 1024;
}
