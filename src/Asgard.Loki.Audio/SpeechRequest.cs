using System.Text.Json.Nodes;

namespace Asgard.Loki.Audio;

/// <summary>描述文本转语音请求，独立于 LLM 协议。</summary>
public sealed record SpeechRequest
{
    /// <summary>对外音频模型路由名。</summary>
    public required string Model { get; init; }
    /// <summary>需要合成的文本。</summary>
    public required string Text { get; init; }
    /// <summary>目标音色。</summary>
    public required string Voice { get; init; }
    /// <summary>输出编码格式。</summary>
    public string Format { get; init; } = "mp3";
    /// <summary>可选采样率，不填写时沿用供应商默认。</summary>
    public int? SampleRate { get; init; }
    /// <summary>保留供应商音频编码扩展参数。</summary>
    public JsonObject AudioParameters { get; init; } = [];
    /// <summary>保留供应商合成参数，不包含认证信息。</summary>
    public JsonObject Parameters { get; init; } = [];
}
