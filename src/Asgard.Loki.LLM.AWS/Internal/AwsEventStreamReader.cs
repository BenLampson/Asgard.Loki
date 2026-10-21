using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.AWS.Internal;

/// <summary>逐帧校验两个 CRC 与长度；先检查本地预算，再分配帧，不拥有来源流。</summary>
internal static class AwsEventStreamReader
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly uint[] CrcTable = MakeCrcTable();
    /// <summary>服务端协议容量限制不作为客户端限制；仅应用显式本地字节策略。</summary>
    internal static async IAsyncEnumerable<AwsEventStreamFrame> ReadAsync(Stream source, int maximumFrame, int maximumTotal, long? expected, [EnumeratorCancellation] CancellationToken token, int maximumHeaders = 128)
    {
        long totalRead = 0;
        while (true)
        {
            var prelude = new byte[12]; var position = 0;
            while (position != 12)
            {
                var count = await source.ReadAsync(prelude.AsMemory(position), token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); totalRead += count;
                if (totalRead > maximumTotal || expected is { } declared && totalRead > declared) throw Invalid();
                if (count == 0)
                {
                    if (position != 0 || expected is { } length && totalRead != length) throw Invalid(); yield break;
                }
                position += count;
            }
            var lengthValue = BinaryPrimitives.ReadUInt32BigEndian(prelude); var headersValue = BinaryPrimitives.ReadUInt32BigEndian(prelude.AsSpan(4));
            if (Crc(prelude.AsSpan(0, 8)) != BinaryPrimitives.ReadUInt32BigEndian(prelude.AsSpan(8))) throw Invalid();
            if (lengthValue < 16 || lengthValue > maximumFrame || headersValue > lengthValue - 16 || totalRead - 12 + lengthValue > maximumTotal || expected is { } size && totalRead - 12 + lengthValue > size) throw Invalid();
            var wire = new byte[(int)lengthValue]; prelude.CopyTo(wire, 0); position = 12;
            while (position != wire.Length)
            {
                var count = await source.ReadAsync(wire.AsMemory(position), token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); if (count == 0) throw Invalid(); totalRead += count; position += count;
            }
            if (Crc(wire.AsSpan(0, wire.Length - 4)) != BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(wire.Length - 4))) throw Invalid();
            yield return new(wire, (int)headersValue, DecodeHeaders(wire.AsSpan(12, (int)headersValue), maximumHeaders));
        }
    }
    /// <summary>保留全部固定头类型；重复头、未知类型及不完整编码均拒绝。</summary>
    private static JsonElement DecodeHeaders(ReadOnlySpan<byte> bytes, int maximumHeaders)
    {
        var headers = new JsonArray(); var names = new HashSet<string>(StringComparer.Ordinal); var position = 0;
        while (position < bytes.Length)
        {
            if (headers.Count >= maximumHeaders) throw Invalid();
            var nameLength = Take(bytes, ref position, 1)[0]; if (nameLength == 0) throw Invalid(); var name = Utf8.GetString(Take(bytes, ref position, nameLength)); if (!names.Add(name)) throw Invalid(); var type = Take(bytes, ref position, 1)[0]; JsonNode? value;
            switch (type)
            {
                case 0: value = JsonValue.Create(true); break;
                case 1: value = JsonValue.Create(false); break;
                case 2: value = JsonValue.Create(unchecked((sbyte)Take(bytes, ref position, 1)[0])); break;
                case 3: value = JsonValue.Create(BinaryPrimitives.ReadInt16BigEndian(Take(bytes, ref position, 2))); break;
                case 4: value = JsonValue.Create(BinaryPrimitives.ReadInt32BigEndian(Take(bytes, ref position, 4))); break;
                case 5: case 8: value = JsonValue.Create(BinaryPrimitives.ReadInt64BigEndian(Take(bytes, ref position, 8))); break;
                case 6: case 7:
                    var length = BinaryPrimitives.ReadUInt16BigEndian(Take(bytes, ref position, 2)); var data = Take(bytes, ref position, length); value = JsonValue.Create(type == 6 ? Convert.ToBase64String(data) : Utf8.GetString(data)); break;
                case 9: value = JsonValue.Create(new Guid(Take(bytes, ref position, 16), bigEndian: true).ToString()); break;
                default: throw Invalid();
            }
            headers.Add(new JsonObject { ["name"] = name, ["type"] = type, ["value"] = value });
        }
        return JsonSerializer.SerializeToElement(headers);
    }
    /// <summary>所有偏移均先检查剩余长度，不依赖底层切片异常放宽畸形头。</summary>
    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> bytes, ref int position, int count)
    {
        if (count > bytes.Length - position) throw Invalid(); var result = bytes.Slice(position, count); position += count; return result;
    }
    /// <summary>标准 CRC32/IEEE，覆盖 prelude 或完整帧中 CRC 之前的全部字节。</summary>
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue; foreach (var value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8); return ~crc;
    }
    /// <summary>固定校验表不保存认证或请求数据。</summary>
    private static uint[] MakeCrcTable()
    {
        var table = new uint[256]; for (uint index = 0; index < table.Length; index++) { var value = index; for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? (value >> 1) ^ 0xedb88320u : value >> 1; table[index] = value; } return table;
    }
    /// <summary>固定错误不回显二进制内容。</summary>
    private static InvalidDataException Invalid() => new("AWS EventStream 长度、CRC 或头编码无效，或超过本地资源策略。");
}
