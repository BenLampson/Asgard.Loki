using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>保留消息顺序、普通工具 ID 和结果种类，不下载图片或把文本猜成 JSON。</summary>
internal static class ConverseTextContent
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    /// <summary>仅合并连续纯工具调用/结果，恢复源 Responses 项对应的同一原生工具轮次。</summary>
    internal static JsonArray WriteMessages(ConversionDocument doc, LlmProtocol source)
    {
        JsonArray messages = []; var calls = new ToolCallTracker();
        foreach (var message in doc.Messages)
        {
            if (message.Role is "system" or "developer") continue;
            if (message.Role is not ("assistant" or "user" or "tool")) throw new ArgumentException("不支持此消息角色。");
            var role = message.Role == "tool" ? "user" : message.Role; JsonArray content = [];
            foreach (var block in message.Blocks)
            {
                var type = Text(block["type"]);
                if (type == "text") content.Add(new JsonObject { ["text"] = Text(block["text"]) });
                else if (type == "image") { if (role != "user") BedrockConversion.Reject("$messages[].content"); content.Add(new JsonObject { ["image"] = Image(block, doc) }); }
                else if (type == "call")
                {
                    if (role != "assistant") throw new ArgumentException("工具调用必须属于 assistant。");
                    var id = Text(block["id"]); var name = Text(block["name"]); calls.Register(id, name);
                    content.Add(new JsonObject { ["toolUse"] = new JsonObject { ["toolUseId"] = id, ["name"] = name, ["input"] = ParseObject(Text(block["arguments"])) } });
                }
                else if (type == "result")
                {
                    if (role != "user") throw new ArgumentException("工具结果必须属于 user/tool。");
                    var id = Text(block["id"]); calls.Consume(id);
                    var result = new JsonObject { ["toolUseId"] = id, ["content"] = Results(block, source, doc) };
                    if (ToolErrorMapping.IsError(block)) result["status"] = "error";
                    content.Add(new JsonObject { ["toolResult"] = result });
                }
                else BedrockConversion.Reject("$messages[].content", "converse_content_not_representable");
            }
            if (content.Count == 0) continue;
            var only = content.All(item => Object(item).ContainsKey(role == "assistant" ? "toolUse" : "toolResult"));
            if (only && messages.LastOrDefault() is JsonObject previous && Text(previous["role"]) == role
                && Array(previous["content"]).All(item => Object(item).ContainsKey(role == "assistant" ? "toolUse" : "toolResult")))
            { foreach (var block in content) previous["content"]!.AsArray().Add(block!.DeepClone()); }
            else messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
        }
        if (messages.Count == 0) throw new ArgumentException("Converse 请求必须包含消息。"); return messages;
    }
    /// <summary>Gemini 对象结果有明确 JSON 类型；其他来源的字符串和文本数组不被解析或拼接。</summary>
    private static JsonArray Results(JsonObject block, LlmProtocol source, ConversionDocument doc)
    {
        JsonArray result = [];
        if (source == LlmProtocol.Gemini)
        {
            if (block.ContainsKey("parts")) BedrockConversion.Reject("$tool_result", "converse_result_not_representable");
            result.Add(new JsonObject { ["json"] = ParseObject(Text(block["output"])) }); return result;
        }
        if (block["parts"] is not JsonArray parts) { result.Add(new JsonObject { ["text"] = Text(block["output"]) }); return result; }
        foreach (var node in parts)
        {
            var part = Object(node);
            if (Text(part["type"]) == "text") result.Add(new JsonObject { ["text"] = Text(part["text"]) });
            else if (Text(part["type"]) == "image") result.Add(new JsonObject { ["image"] = Image(part, doc) });
            else BedrockConversion.Reject("$tool_result.content[]", "converse_result_not_representable");
        }
        return result;
    }
    /// <summary>只使用调用者明确提供的标准 Base64 图片，不解析 URL、S3 或其他供应商文件 ID。</summary>
    internal static JsonObject Image(JsonObject block, ConversionDocument doc)
    {
        var url = Text(block["url"]); var comma = url.IndexOf(',');
        if (comma < 0 || !url[..comma].EndsWith(";base64", StringComparison.Ordinal)) BedrockConversion.Reject("$content.image", "converse_media_reference_not_representable");
        var format = url[..comma] switch { "data:image/png;base64" => "png", "data:image/jpeg;base64" => "jpeg", "data:image/gif;base64" => "gif", "data:image/webp;base64" => "webp", _ => null };
        if (format is null) BedrockConversion.Reject("$content.image", "converse_media_type_not_representable");
        var bytes = url[(comma + 1)..]; if (!Base64.IsValid(bytes.AsSpan(), out var length) || length == 0) throw new ArgumentException("内联图片必须是非空标准 Base64。");
        if (block.ContainsKey("detail")) Loss(doc, "$content.image.detail", "converse_image_detail_not_representable");
        return new() { ["format"] = format, ["source"] = new JsonObject { ["bytes"] = bytes } };
    }
    /// <summary>完整参数/对象结果拒绝重复属性和非法 Unicode，保留原始数值，不修补为其他 JSON。</summary>
    internal static JsonObject ParseObject(string text)
    {
        using var json = JsonDocument.Parse(text, new JsonDocumentOptions { AllowDuplicateProperties = false });
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("工具参数/对象结果必须是 JSON 对象。");
        ValidateUnicode(json.RootElement); return JsonNode.Parse(json.RootElement.GetRawText())!.AsObject();
    }
    /// <summary>属性名和字符串只做编码验证，不按业务键解释错误、签名或执行状态。</summary>
    private static void ValidateUnicode(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object) foreach (var field in value.EnumerateObject()) { _ = StrictUtf8.GetByteCount(field.Name); ValidateUnicode(field.Value); }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) ValidateUnicode(item);
        else if (value.ValueKind == JsonValueKind.String) _ = StrictUtf8.GetByteCount(value.GetString()!);
    }
}
