using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.AWS.Internal;

/// <summary>供应商内部严格编码和有限 JSON 读取，不映射消息或工具关联。</summary>
internal static class AwsJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>克隆、URI 和序列化之前拒绝字符替代。</summary>
    internal static void Text(string value) => _ = Utf8.GetByteCount(value);
    /// <summary>检查原始 DOM 字符串和属性名；自定义转换器策略仍由调用者负责。</summary>
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
    /// <summary>按原始字节限制响应和错误正文；原始流由响应拥有。</summary>
    internal static async Task<byte[]> ReadAsync(Stream source, int maximum, long? expected, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192); var output = new ArrayBufferWriter<byte>();
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) != 0)
            {
                var length = (long)output.WrittenCount + count;
                if (length > maximum || expected is { } size && length > size) throw new InvalidDataException("Bedrock 正文超过原始字节上限或声明长度。");
                output.Write(buffer.AsSpan(0, count));
            }
            token.ThrowIfCancellationRequested(); if (expected is { } declared && output.WrittenCount != declared) throw new InvalidDataException("Bedrock 正文没有达到声明长度。");
            return output.WrittenMemory.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    /// <summary>原始错误文本也不使用宽松编码修复。</summary>
    internal static string Decode(byte[] bytes) => Utf8.GetString(bytes);
    /// <summary>重复属性和未知字段中的无效转义仍必须失败。</summary>
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
        catch (InvalidOperationException error) { throw new InvalidDataException("Bedrock JSON 含无效字符转义。", error); }
    }
}
