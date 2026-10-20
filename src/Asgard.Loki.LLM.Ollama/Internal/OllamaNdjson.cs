using System.Runtime.CompilerServices;
using System.Text.Json;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.LLM.Ollama.Internal;

/// <summary>共享原始记录分帧，原生 JSON 和空白行语义仍由 Ollama 自己解释。</summary>
internal static class OllamaNdjson
{
    /// <summary>每个元素是完整独立原生对象，不产生 SSE 身份或自动重连。</summary>
    internal static async IAsyncEnumerable<JsonElement> ReadAsync(Stream source, int maximum,
        [EnumeratorCancellation] CancellationToken token, long? expectedLength = null)
    {
        await foreach (var line in NdjsonReader.ReadAsync(source, maximum, long.MaxValue, token, expectedLength).ConfigureAwait(false))
            if (Parse(line) is { } record) yield return record;
    }
    /// <summary>允许空白分隔行和末记录无换行；空白不是原生事件，不能替代 done。</summary>
    private static JsonElement? Parse(ReadOnlyMemory<byte> line)
    {
        foreach (var value in line.Span) if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r')) return OllamaJson.Parse(line);
        return null;
    }
}
