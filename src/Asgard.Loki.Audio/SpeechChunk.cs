using System.Text.Json;

namespace Asgard.Loki.Audio;

/// <summary>统一的音频流分块，同时保留供应商完整元数据。</summary>
public sealed class SpeechChunk
{
    /// <summary>复制音频和元数据所有权。</summary>
    public SpeechChunk(ReadOnlyMemory<byte> audio, bool isCompleted, JsonElement data, string providerId)
    {
        Audio = audio.ToArray();
        IsCompleted = isCompleted;
        Data = data.Clone();
        ProviderId = providerId;
    }

    /// <summary>有序音频字节，元数据帧允许为空。</summary>
    public ReadOnlyMemory<byte> Audio { get; }
    /// <summary>供应商是否已经确认本次合成完成。</summary>
    public bool IsCompleted { get; }
    /// <summary>完整的供应商帧，包括用量、字幕和扩展信息。</summary>
    public JsonElement Data { get; }
    /// <summary>执行合成的供应商实例身份。</summary>
    public string ProviderId { get; }
}
