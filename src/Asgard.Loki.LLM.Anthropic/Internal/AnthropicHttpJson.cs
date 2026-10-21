using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Anthropic.Internal;

/// <summary>严格 JSON/UTF-8 工具，不映射消息、工具、过滤结果或用量。</summary>
internal static class AnthropicHttpJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>不让非法字符在 URI、JSON 或指纹中被替代。</summary>
    internal static void Text(string value) => _ = Utf8.GetByteCount(value);
    /// <summary>克隆和序列化前检查原生 DOM，保留未知成员。</summary>
    internal static void Input(JsonNode root)
    {
        var pending = new Stack<JsonNode?>(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node is JsonObject obj) foreach (var property in obj) { Text(property.Key); pending.Push(property.Value); }
            else if (node is JsonArray array) foreach (var child in array) pending.Push(child);
            else if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text)) Text(text);
        }
    }
    /// <summary>按原始字节限制正文并检查声明长度；流仍由响应拥有。</summary>
    internal static async Task<byte[]> ReadAsync(Stream source, int maximum, long? expected, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192); var output = new ArrayBufferWriter<byte>();
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) != 0)
            {
                var length = (long)output.WrittenCount + count;
                if (length > maximum || expected is { } declared && length > declared) throw new InvalidDataException("Claude 正文超过配置上限或声明长度。");
                output.Write(buffer.AsSpan(0, count));
            }
            token.ThrowIfCancellationRequested(); if (expected is { } size && output.WrittenCount != size) throw new InvalidDataException("Claude 正文没有达到声明长度。");
            return output.WrittenMemory.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    /// <summary>错误原文也使用严格编码。</summary>
    internal static string Decode(byte[] bytes) => Utf8.GetString(bytes);
    /// <summary>重复属性、非法编码和未知字符串转义不能悄悄通过。</summary>
    internal static JsonElement Parse(byte[] bytes)
    {
        _ = Utf8.GetCharCount(bytes);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false }); var pending = new Stack<JsonElement>(); pending.Push(document.RootElement);
            while (pending.TryPop(out var value))
            {
                if (value.ValueKind == JsonValueKind.String) _ = value.GetString();
                else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) pending.Push(child);
                else if (value.ValueKind == JsonValueKind.Object) foreach (var property in value.EnumerateObject()) { _ = property.Name; pending.Push(property.Value); }
            }
            return document.RootElement.Clone();
        }
        catch (InvalidOperationException error) { throw new InvalidDataException("Claude JSON 含无效字符串转义。", error); }
    }
    /// <summary>只识别原生根级错误，不遍历业务函数参数。</summary>
    internal static bool IsError(JsonElement data) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null;
}
