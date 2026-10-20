using System.Text.Json;

namespace Asgard.Loki.Audio.Volcengine.Internal;

/// <summary>解码后独立拥有的 V1 原生报文，不估算音频用量。</summary>
internal sealed record V1SpeechPacket(byte[] Raw, byte[] Payload, int Type, int Flags,
    int Serialization, int Compression, int? Sequence, uint? ErrorCode, JsonElement? NativeData)
{
    /// <summary>二进制记录明确标识为 SDK 传输封装，原报文和 JSON 均保留。</summary>
    internal JsonElement Metadata(string requestId) => JsonSerializer.SerializeToElement(new
    {
        kind = "volcengine.v1.websocket", requestId, messageType = Type, flags = Flags,
        serialization = Serialization, compression = Compression, sequence = Sequence,
        rawFrameBase64 = Convert.ToBase64String(Raw), payloadBase64 = Convert.ToBase64String(Payload), nativeData = NativeData
    });
}
