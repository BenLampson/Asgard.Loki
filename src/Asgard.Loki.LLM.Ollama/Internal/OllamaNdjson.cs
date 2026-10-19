using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Asgard.Loki.LLM.Ollama.Internal;

/// <summary>仅按原生 LF/CRLF 记录分割；有界缓冲可跨任意 UTF-8/网络碎片。</summary>
internal static class OllamaNdjson
{
    /// <summary>每个元素是完整独立原生对象，不产生 SSE 身份或自动重连。</summary>
    internal static async IAsyncEnumerable<JsonElement> ReadAsync(Stream source, int maximum,
        [EnumeratorCancellation] CancellationToken token, long? expectedLength = null)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192); var line = new ArrayBufferWriter<byte>(Math.Min(maximum, 4096));
        try
        {
            int count; long received = 0;
            while ((count = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) != 0)
            {
                received = checked(received + count);
                if (expectedLength is { } length && received > length) throw new InvalidDataException("Ollama 流超过声明长度。");
                for (var i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested(); var value = buffer[i];
                    if (value == (byte)'\n')
                    {
                        var item = Parse(line.WrittenMemory); line.Clear(); if (item is { } record) yield return record; continue;
                    }
                    if (line.WrittenCount >= maximum) throw new InvalidDataException("Ollama NDJSON 记录超过配置的字节上限。");
                    line.GetSpan(1)[0] = value; line.Advance(1);
                }
            }
            token.ThrowIfCancellationRequested();
            if (expectedLength is { } expected && received != expected) throw new InvalidDataException("Ollama 流没有达到声明长度。");
            if (Parse(line.WrittenMemory) is { } last) yield return last;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    /// <summary>允许空白分隔行和末记录无换行；空白不是原生事件，不能替代 done。</summary>
    private static JsonElement? Parse(ReadOnlyMemory<byte> line)
    {
        foreach (var value in line.Span) if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r')) return OllamaJson.Parse(line);
        return null;
    }
}
