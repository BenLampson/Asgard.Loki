using System.Buffers.Text;
using System.Text;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>只包装明确 Anthropic/Claude 的原生签名与加密联合分支，不验证密码学身份或跨模型移植状态。</summary>
internal static class ConverseAnthropicReasoning
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    /// <summary>私有块类型不能由公共输入直接构造；只有本编解码器产生。</summary>
    internal const string Kind = "converse_anthropic_reasoning";
    /// <summary>Claude 历史只接受 assistant 的完整原生 thinking/redacted 块。</summary>
    internal static JsonObject ReadClaude(JsonObject source, string role)
    {
        if (role != "assistant") throw new ArgumentException("Anthropic 推理历史必须属于 assistant。");
        var type = Text(source["type"]); JsonObject native;
        if (type == "thinking")
        {
            Exact(source, "type", "thinking", "signature");
            native = new() { ["reasoningText"] = new JsonObject { ["text"] = Value(source["thinking"]), ["signature"] = Value(source["signature"]) } };
        }
        else if (type == "redacted_thinking")
        {
            Exact(source, "type", "data"); native = new() { ["redactedContent"] = Redacted(source["data"]) };
        }
        else throw new ArgumentException("只允许完整 Claude 原生推理块。");
        return Canonical(native);
    }
    /// <summary>Converse 联合分支及其内部对象必须完整、唯一且没有无法携带的扩展。</summary>
    internal static JsonObject ReadNative(JsonObject source)
    {
        Exact(source, "reasoningContent"); var native = Object(source["reasoningContent"]);
        if (native.ContainsKey("reasoningText"))
        {
            Exact(native, "reasoningText"); var text = Object(native["reasoningText"]); Exact(text, "text", "signature");
            _ = Value(text["text"]); _ = Value(text["signature"]);
        }
        else
        {
            Exact(native, "redactedContent"); _ = Redacted(native["redactedContent"]);
        }
        return Canonical(native);
    }
    /// <summary>重新验证内部形状后写出整块，不把不透明状态当文本或普通 JSON 工具参数。</summary>
    internal static JsonObject WriteNative(JsonObject block, string role)
    {
        if (role != "assistant") throw new ArgumentException("Anthropic 推理内容必须属于 assistant。");
        var output = new JsonObject { ["reasoningContent"] = Object(block["data"]).DeepClone() };
        _ = ReadNative(output); return output;
    }
    /// <summary>反向只投影到 Claude 的同一原生块，不产生 Gemini/OpenAI 的虚构签名或推理字段。</summary>
    internal static JsonObject WriteClaude(JsonObject block)
    {
        var source = Object(block["data"]); _ = ReadNative(new() { ["reasoningContent"] = source.DeepClone() });
        if (source["reasoningText"] is JsonObject text)
            return new() { ["type"] = "thinking", ["thinking"] = Value(text["text"]), ["signature"] = Value(text["signature"]) };
        return new() { ["type"] = "redacted_thinking", ["data"] = Redacted(source["redactedContent"]) };
    }
    /// <summary>所有节点都由当前调用独立持有，不保留源对象可变引用。</summary>
    private static JsonObject Canonical(JsonObject data) => new() { ["type"] = Kind, ["data"] = data.DeepClone() };
    /// <summary>字符串只校验有效 Unicode，不裁剪空文本、空白、换行或签名。</summary>
    private static string Value(JsonNode? node)
    {
        var text = Text(node); _ = StrictUtf8.GetByteCount(text); return text;
    }
    /// <summary>标准 Base64 只验证语法及非空字节，不解码、重新编码或解释加密内容。</summary>
    private static string Redacted(JsonNode? node)
    {
        var text = Value(node);
        if (!Base64.IsValid(text.AsSpan(), out var length) || length == 0) throw new ArgumentException("加密推理块必须提供非空原生 Base64。");
        return text;
    }
    /// <summary>不透明签名结构的未知/缺失/混合字段不能因普通损耗许可而被静默移除。</summary>
    private static void Exact(JsonObject value, params string[] keys)
    {
        if (value.Count != keys.Length || keys.Any(key => !value.ContainsKey(key)))
            BedrockConversion.Reject("$content.reasoning", "converse_anthropic_reasoning_not_representable");
    }
}
