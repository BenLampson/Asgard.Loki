using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Audio.Cloudflare.Internal;

/// <summary>严格验证整个供应商 JSON，不丢弃未知数值、字段或无效字符串。</summary>
internal static class CloudflareAudioJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>拒绝无效代理项，不替换原始文本。</summary>
    internal static void Text(string value) => _ = Utf8.GetByteCount(value);
    /// <summary>严格计算原生参数的 UTF-8 字节长度。</summary>
    internal static int TextBytes(string value) => Utf8.GetByteCount(value);
    /// <summary>按原始 UTF-8 读取 JSON 并复制所有权。</summary>
    internal static JsonElement Parse(byte[] bytes, bool requireObject = true)
    {
        try
        {
            _ = Utf8.GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (requireObject && document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Cloudflare 音频 JSON 必须为对象。");
            Read(document.RootElement); return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or DecoderFallbackException)
        { throw new InvalidDataException("Cloudflare 音频 JSON 或 Unicode 无效。", error); }
    }
    private static void Read(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) Text(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) Read(child);
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var member in value.EnumerateObject()) { Text(member.Name); Read(member.Value); }
    }
    /// <summary>整个返回对象保留原文，文本允许为空以表示没有可识别语音。</summary>
    internal static void Result(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Cloudflare 音频结果缺少原生 text 字符串。");
    }
}
