using System.Text.Json.Serialization;

namespace Asgard.Loki.Audio.Volcengine;

/// <summary>独立配置火山语音认证、资源和请求地址。</summary>
public sealed class VolcengineSpeechOptions
{
    /// <summary>当前适配实例的稳定标识。</summary>
    public string Id { get; init; } = "volcengine-speech";
    /// <summary>火山语音资源 ID，与所用音色的授权匹配。</summary>
    public required string ResourceId { get; init; }
    /// <summary>新版语音控制台 API Key。</summary>
    [JsonIgnore]
    public string? ApiKey { get; init; }
    /// <summary>旧版控制台应用 ID，仅与 AccessKey 配合使用。</summary>
    public string? AppId { get; init; }
    /// <summary>旧版控制台访问令牌。</summary>
    [JsonIgnore]
    public string? AccessKey { get; init; }
    /// <summary>HTTP 单向流式接口，不支持通过本选项切换到其他协议。</summary>
    public Uri Endpoint { get; init; } = new("https://openspeech.bytedance.com/api/v3/tts/unidirectional");
    /// <summary>外部 HTTP 工厂中注册的名称。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Audio.Volcengine";
}
