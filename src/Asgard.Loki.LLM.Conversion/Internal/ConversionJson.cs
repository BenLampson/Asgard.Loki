using System.Text.Json.Nodes;
using System.Text.Json;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>检测未识别字段及非法结构，禁止静默省略内容。</summary>
internal static class ConversionJson
{
    /// <summary>读取必需的字符串。</summary>
    internal static string Text(JsonNode? node) => node?.GetValue<string>() ?? throw new ArgumentException("缺少必需的字符串字段。");
    /// <summary>按 JSON 数值而非 JsonValue 的 CLR 装箱类型读取整数。</summary>
    internal static long Number(JsonNode? node) => node is null ? 0 : JsonSerializer.SerializeToElement(node).GetInt64();
    /// <summary>读取显式布尔字段，null 不是 false，也不能触发空引用异常。</summary>
    internal static bool Boolean(JsonNode? node) => node?.GetValue<bool>() ?? throw new ArgumentException("协议字段必须是布尔值。");
    /// <summary>读取对象。</summary>
    internal static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw new ArgumentException("协议字段必须是对象。");
    /// <summary>读取数组。</summary>
    internal static JsonArray Array(JsonNode? node) => node as JsonArray ?? throw new ArgumentException("协议字段必须是数组。");
    /// <summary>为所有不支持的字段记录诊断，显式 null 同样不忽略。</summary>
    internal static void Check(JsonObject node, ConversionDocument doc, string path, params string[] supported)
    {
        foreach (var field in node)
            if (!supported.Contains(field.Key, StringComparer.Ordinal)) Loss(doc, path + "." + field.Key);
    }
    /// <summary>记录无法等价表示的字段，不包含原始用户数据。</summary>
    internal static void Loss(ConversionDocument doc, string path, string code = "unsupported_field")
        => doc.Diagnostics.Add(new(code, path, "目标协议无法等价表达此字段或内容。"));
    /// <summary>复制存在的值，区分省略与 null。</summary>
    internal static void Copy(JsonObject from, string key, JsonObject to, string target)
    {
        if (from.TryGetPropertyValue(key, out var value)) to[target] = value?.DeepClone();
    }
    /// <summary>仅允许明确的单一原生协议值。</summary>
    internal static void ValidateProtocol(LlmProtocol protocol)
    {
        if (protocol is not (LlmProtocol.ChatCompletions or LlmProtocol.Responses or LlmProtocol.ClaudeMessages or LlmProtocol.Gemini
            or LlmProtocol.BedrockConverse or LlmProtocol.BedrockNova))
            throw new ArgumentOutOfRangeException(nameof(protocol));
    }
}
