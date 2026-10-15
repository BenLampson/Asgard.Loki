namespace Asgard.Loki.Audio;

/// <summary>组织独立的音频能力入口。</summary>
public sealed class AudioClient
{
    /// <summary>接收已完成配置校验的语音客户端。</summary>
    internal AudioClient(ISpeechClient speech) => Speech = speech;
    /// <summary>文本转语音能力。</summary>
    public ISpeechClient Speech { get; }
}
