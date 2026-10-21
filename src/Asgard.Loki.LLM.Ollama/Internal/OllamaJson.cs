using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.LLM.Ollama.Internal;

/// <summary>有界读取和严格 UTF-8 JSON，不以替代字符、重复属性或截断正文修复协议。</summary>
internal static class OllamaJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    /// <summary>纯 JSON 错误判定由同供应商的原生操作共用，不共享业务状态。</summary>
    internal static bool IsError(JsonElement data) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null;
    /// <summary>读取完整字节前逐段验证上限；响应拥有流，读取器不单独接管所有权。</summary>
    internal static async Task<byte[]> ReadAsync(Stream source, int maximum, CancellationToken token, long? expectedLength = null)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192); var output = new ArrayBufferWriter<byte>();
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) != 0)
            {
                if ((long)output.WrittenCount + count > maximum) throw new InvalidDataException("Ollama 完整正文超过配置的字节上限。");
                if (expectedLength is { } length && (long)output.WrittenCount + count > length) throw new InvalidDataException("Ollama 正文超过声明长度。");
                output.Write(buffer.AsSpan(0, count));
            }
            token.ThrowIfCancellationRequested();
            if (expectedLength is { } expected && output.WrittenCount != expected) throw new InvalidDataException("Ollama 正文没有达到声明长度。");
            return output.WrittenMemory.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    /// <summary>所有字段的 UTF-8 都先验证；未知字符串也不能静默替换，再保存独立 JSON 快照。</summary>
    internal static JsonElement Parse(ReadOnlyMemory<byte> bytes)
    {
        try { _ = StrictUtf8.GetCharCount(bytes.Span); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Ollama 响应不是有效 UTF-8。", error); }
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            ValidateStrings(json.RootElement); return json.RootElement.Clone();
        }
        catch (InvalidOperationException error) { throw new InvalidDataException("Ollama JSON 包含无效 Unicode 转义。", error); }
    }
    /// <summary>JSON 语法读取不一定解码未知字符串，主动验证转义后的所有字段与属性名。</summary>
    private static void ValidateStrings(JsonElement root)
    {
        var pending = new Stack<JsonElement>(); pending.Push(root);
        while (pending.TryPop(out var value))
        {
            if (value.ValueKind == JsonValueKind.String) _ = value.GetString();
            else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) pending.Push(child);
            else if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject()) { _ = property.Name; pending.Push(property.Value); }
        }
    }
    /// <summary>直接适配器调用也在深拷贝和序列化前拒绝无效 Unicode，不改变未知字段。</summary>
    internal static void ValidateInput(System.Text.Json.Nodes.JsonNode value)
    {
        var pending = new Stack<System.Text.Json.Nodes.JsonNode?>(); pending.Push(value);
        while (pending.TryPop(out var node))
        {
            if (node is System.Text.Json.Nodes.JsonObject obj)
                foreach (var property in obj) { _ = StrictUtf8.GetByteCount(property.Key); pending.Push(property.Value); }
            else if (node is System.Text.Json.Nodes.JsonArray array) foreach (var child in array) pending.Push(child);
            else if (node is System.Text.Json.Nodes.JsonValue scalar && scalar.TryGetValue<string>(out var text)) _ = StrictUtf8.GetByteCount(text);
        }
    }
    /// <summary>错误正文同样使用严格编码，不截断或重写原始错误。</summary>
    internal static string Text(ReadOnlySpan<byte> bytes)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Ollama 响应不是有效 UTF-8。", error); }
    }
}
