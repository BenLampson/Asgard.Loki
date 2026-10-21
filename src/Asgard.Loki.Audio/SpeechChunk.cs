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
    /// <summary>供应商适配器是否已依据自身协议/传输规则确认本次合成完成。</summary>
    public bool IsCompleted { get; }
    /// <summary>完整供应商帧或明确标识的 SDK 传输记录；不保证二进制上游提供用量/字幕。</summary>
    public JsonElement Data { get; }
    /// <summary>执行合成的供应商实例身份。</summary>
    public string ProviderId { get; }
}
