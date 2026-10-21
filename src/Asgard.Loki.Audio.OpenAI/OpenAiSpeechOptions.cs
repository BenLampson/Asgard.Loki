using System.Text.Json.Serialization;

namespace Asgard.Loki.Audio.OpenAI;

/// <summary>配置完整 OpenAI 语音目标，不复用 LLM 或其他音频供应商的身份。</summary>
public sealed class OpenAiSpeechOptions
{
    /// <summary>固定适配实例标识。</summary>
    public string Id { get; init; } = "openai-speech";
    /// <summary>实际语音模型名，不追加后缀或根据名称修改参数。</summary>
    public required string Model { get; init; }
    /// <summary>调用者提供的 API Key，不参与 JSON 序列化。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>包含版本路径的 HTTP(S) API 根地址。</summary>
    public Uri BaseAddress { get; init; } = new("https://api.openai.com/v1/");
    /// <summary>可选组织请求头。</summary>
    public string? Organization { get; init; }
    /// <summary>可选项目请求头。</summary>
    public string? Project { get; init; }
    /// <summary>外部工厂注册的客户端名称。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Audio.OpenAI";
    /// <summary>单次本地读取缓冲区，范围 1 至 1 MiB；不表示音频帧大小或总长度上限。</summary>
    public int ReadBufferSize { get; init; } = 8192;
}
