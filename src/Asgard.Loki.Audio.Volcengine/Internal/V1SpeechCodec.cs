using System.Buffers.Binary;
using System.IO.Compression;

namespace Asgard.Loki.Audio.Volcengine.Internal;

/// <summary>仅处理 V1 四字节头单次语音协议，不接入 V3 事件或会话。</summary>
internal static class V1SpeechCodec
{
    /// <summary>JSON 序列化类型明确为 1，长度使用网络字节序。</summary>
    internal static byte[] Encode(byte[] json, bool compress)
    {
        var payload = json;
        if (compress)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(json);
            payload = output.ToArray();
        }
        var packet = new byte[checked(8 + payload.Length)];
        packet[0] = 0x11; packet[1] = 0x10; packet[2] = (byte)(0x10 | (compress ? 1 : 0));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), (uint)payload.Length); payload.CopyTo(packet, 8);
        return packet;
    }
    /// <summary>校验完整报文、序号符号、压缩和 JSON 元数据，不静默跳过未知类型。</summary>
    internal static V1SpeechPacket Decode(byte[] raw, int maximum)
    {
        if (raw.Length < 8 || raw[0] != 0x11 || raw[3] != 0) throw new InvalidDataException("V1 二进制头无效。");
        int type = raw[1] >> 4, flags = raw[1] & 15, serialization = raw[2] >> 4, compression = raw[2] & 15;
        var offset = 4; int? sequence = null; uint? code = null;
        if (type == 11)
        {
            if (flags > 3 || serialization != 0) throw new InvalidDataException("V1 音频头无效。");
            if (flags != 0)
            {
                if (raw.Length < 12) throw new InvalidDataException("V1 序号缺失。");
                sequence = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(offset)); offset += 4;
                if (flags == 1 ? sequence <= 0 : sequence >= 0) throw new InvalidDataException("V1 音频序号与标志不一致。");
            }
        }
        else if (type == 15)
        {
            if (flags != 0 || serialization > 1 || raw.Length < 12) throw new InvalidDataException("V1 错误头无效。");
            code = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(offset)); offset += 4;
        }
        else if (type != 12 || flags != 0 || serialization > 1) throw new InvalidDataException("不支持此 V1 响应类型或标志。");
        var length = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(offset)); offset += 4;
        if (length != raw.Length - offset) throw new InvalidDataException("V1 负载长度不匹配。");
        var payload = compression switch
        {
            0 => raw.AsSpan(offset).ToArray(),
            1 => Inflate(raw.AsSpan(offset).ToArray(), maximum),
            _ => throw new InvalidDataException("不支持此 V1 压缩方式。")
        };
        if (payload.Length > maximum) throw new InvalidDataException("V1 解码负载超过上限。");
        // 错误负载允许为任意原始字节；不因 JSON 声明损坏而丢失原始错误码。
        var data = type == 12 && serialization == 1 ? V1SpeechJson.Parse(payload) : (System.Text.Json.JsonElement?)null;
        return new(raw, payload, type, flags, serialization, compression, sequence, code, data);
    }
    private static byte[] Inflate(byte[] bytes, int maximum)
    {
        if (bytes.Length < 18 || bytes[0] != 31 || bytes[1] != 139 || bytes[2] != 8) throw new InvalidDataException("V1 gzip 头或尾不完整。");
        using var input = new MemoryStream(bytes, false); using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(); var buffer = new byte[Math.Min(8192, maximum)]; int read;
        while ((read = gzip.Read(buffer)) != 0)
        {
            if (read > maximum - output.Length) throw new InvalidDataException("V1 gzip 解压超过上限。");
            output.Write(buffer, 0, read);
        }
        var result = output.ToArray();
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4)) != (uint)result.Length
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 8)) != Crc32(result))
            throw new InvalidDataException("V1 gzip 校验和或解码长度不匹配。");
        return result;
    }
    private static uint Crc32(byte[] bytes)
    {
        uint crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        return ~crc;
    }
}
