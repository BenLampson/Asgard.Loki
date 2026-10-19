using System.Text.Json.Serialization;

namespace Asgard.Loki.Audio.OpenAI;

/// <summary>原生文件转录/翻译完整目标，与 TTS/LLM 目标的模型和凭据分别配置。</summary>
public sealed class OpenAiAudioFileOptions
{
    /// <summary>稳定的完整目标身份。</summary>
    public string Id { get; init; } = "openai-audio-file";
    /// <summary>实际文件操作模型，不按名称改变字段或推断账号授权。</summary>
    public required string Model { get; init; }
    /// <summary>调用者提供的凭据，不参与 JSON 序列化。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>包含版本路径的 HTTP(S) API 根地址，不包含认证、查询或片段。</summary>
    public Uri BaseAddress { get; init; } = new("https://api.openai.com/v1/");
    /// <summary>可选组织身份请求头。</summary>
    public string? Organization { get; init; }
    /// <summary>可选项目身份请求头。</summary>
    public string? Project { get; init; }
    /// <summary>外部工厂注册的命名客户端。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Audio.OpenAI.File";
}
