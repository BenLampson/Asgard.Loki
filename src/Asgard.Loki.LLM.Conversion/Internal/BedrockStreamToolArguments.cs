using System.Text;
using System.Text.Json;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>按原始索引维护单个工具的有界参数，验证完成对象但不重写增量。</summary>
internal sealed class BedrockStreamToolArguments(int maximumBytes)
{
    private readonly StringBuilder _text = new();
    private long _bytes;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    /// <summary>先按实际参数文本的 UTF-8 字节校验，再扩展聚合缓冲区。</summary>
    internal void Append(string value, string path)
    {
        var bytes = StrictUtf8.GetByteCount(value);
        if (_bytes + bytes > maximumBytes) BedrockConversion.Reject(path, "bedrock_stream_argument_limit");
        _bytes += bytes; _text.Append(value);
    }
    /// <summary>工具停止前必须形成完整对象，空、截断、重复键和无效编码均不能被修补。</summary>
    internal void Complete()
    {
        try
        {
            using var document = JsonDocument.Parse(_text.ToString());
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            Validate(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or EncoderFallbackException)
        { throw new InvalidDataException("Bedrock 工具参数不是完整有效的原始 JSON 对象。", error); }
    }
    /// <summary>仅验证参数 JSON 本身，不按 error、签名或业务属性名解释参数语义。</summary>
    private static void Validate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            {
                _ = StrictUtf8.GetByteCount(field.Name); if (!names.Add(field.Name)) throw Invalid(); Validate(field.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Validate(item);
        else if (value.ValueKind == JsonValueKind.String) _ = StrictUtf8.GetByteCount(value.GetString()!);
    }
    /// <summary>固定异常不回显工具参数或原始业务内容。</summary>
    private static InvalidDataException Invalid() => new("Bedrock 工具参数结构、重复属性或编码无效。");
}
