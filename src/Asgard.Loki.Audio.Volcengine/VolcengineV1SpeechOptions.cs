using System.Text.Json.Serialization;

namespace Asgard.Loki.Audio.Volcengine;

/// <summary>固定 V1 语音目标、认证及本地资源上限，不读取环境凭据。</summary>
public sealed class VolcengineV1SpeechOptions
{
    /// <summary>供应商实例身份。</summary>
    public string Id { get; init; } = "volcengine-v1";
    /// <summary>目标应用标识。</summary>
    public required string AppId { get; init; }
    /// <summary>目标原生集群。</summary>
    public required string Cluster { get; init; }
    /// <summary>仅用于 Authorization 请求头的访问令牌。</summary>
    [JsonIgnore]
    public string AccessToken { get; init; } = "";
    /// <summary>必须显式指定 HTTP 或 WebSocket。</summary>
    public required VolcengineV1SpeechTransport Transport { get; init; }
    /// <summary>可选完整接口地址；为空时使用所选传输的官方 V1 地址。</summary>
    public Uri? Endpoint { get; init; }
    /// <summary>借用 HTTP 工厂的命名客户端。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Audio.Volcengine.V1";
    /// <summary>WebSocket 请求是否使用 gzip，不改变 JSON 语义。</summary>
    public bool CompressRequest { get; init; }
    /// <summary>请求 JSON 最大字节数。</summary>
    public int MaxRequestBytes { get; init; } = 1024 * 1024;
    /// <summary>单条完整响应消息或 HTTP 响应最大字节数。</summary>
    public int MaxMessageBytes { get; init; } = 4 * 1024 * 1024;
    /// <summary>单条解压负载或 HTTP 音频最大字节数。</summary>
    public int MaxDecodedBytes { get; init; } = 8 * 1024 * 1024;
    /// <summary>一次 WebSocket 调用的线上及解码累计字节数上限，分别计数。</summary>
    public long MaxResponseBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>一次 WebSocket 调用允许的最大消息数，包括空 ACK。</summary>
    public int MaxFrames { get; init; } = 4096;
    /// <summary>发送关闭后等待对端关闭的最长时间。</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
