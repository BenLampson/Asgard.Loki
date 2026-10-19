using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>转换明确可表达的内容块；推理签名、加密项及未知内容始终诊断。</summary>
internal static class ContentMapping
{
    /// <summary>按原生协议读取文本、图片、函数调用与结果。</summary>
    internal static void Read(JsonNode? content, LlmProtocol protocol, ConversionMessage message, ConversionDocument doc, bool mapCitations = false)
    {
        if (content is null) return;
        if (content is JsonValue value)
        {
            message.Blocks.Add(new() { ["type"] = "text", ["text"] = value.GetValue<string>() });
            return;
        }
        var blocks = Array(content);
        if (protocol == LlmProtocol.Gemini) ReserveGeminiIds(blocks, doc);
        foreach (var node in blocks)
        {
            var block = Object(node);
            if (protocol == LlmProtocol.Gemini) { Gemini(block, message, doc); continue; }
            var type = Text(block["type"]);
            switch (type)
            {
                case "text": case "input_text": case "output_text":
                    Check(block, doc, "$content[]", "type", "text", "annotations");
                    var textBlock = new JsonObject { ["type"] = "text", ["text"] = Text(block["text"]) };
                    if (block.ContainsKey("annotations"))
                    {
                        if (mapCitations && protocol == LlmProtocol.Responses)
                        {
                            var citations = ResponseCitations.Read(block["annotations"], Text(block["text"]), protocol, doc, "$content[].annotations");
                            if (citations.Count != 0) textBlock["citations"] = citations;
                        }
                        else if (Array(block["annotations"]).Count != 0) Loss(doc, "$content[].annotations");
                    }
                    message.Blocks.Add(textBlock);
                    break;
                case "image_url": case "input_image":
                    Check(block, doc, "$content[]", block["image_url"] is JsonObject ? ["type", "image_url"] : ["type", "image_url", "detail"]);
                    var image = new JsonObject { ["type"] = "image" };
                    if (block["image_url"] is JsonObject url)
                    {
                        Check(url, doc, "$content[].image_url", "url", "detail");
                        image["url"] = Text(url["url"]);
                        Copy(url, "detail", image, "detail");
                    }
                    else { image["url"] = Text(block["image_url"]); Copy(block, "detail", image, "detail"); }
                    message.Blocks.Add(image);
                    break;
                case "image":
                    Check(block, doc, "$content[]", "type", "source");
                    var source = Object(block["source"]);
                    var sourceType = Text(source["type"]);
                    Check(source, doc, "$content[].source", sourceType == "url" ? ["type", "url"] : ["type", "media_type", "data"]);
                    if (sourceType is not ("base64" or "url")) { Loss(doc, "$content[].source"); break; }
                    message.Blocks.Add(new() { ["type"] = "image", ["url"] = sourceType == "url" ? Text(source["url"])
                        : "data:" + Text(source["media_type"]) + ";base64," + Text(source["data"]) });
                    break;
                case "file": case "input_file": case "document":
                    InputFileMapping.Read(block, protocol, message, doc); break;
                case "tool_use":
                    Check(block, doc, "$content[]", "type", "id", "name", "input");
                    message.Blocks.Add(new() { ["type"] = "call", ["id"] = Text(block["id"]), ["name"] = Text(block["name"]), ["arguments"] = Object(block["input"]).ToJsonString() });
                    break;
                case "tool_result":
                    Check(block, doc, "$content[]", "type", "tool_use_id", "content", "is_error");
                    ToolResultMapping.ClaudeError(block);
                    message.Blocks.Add(ToolResultMapping.Read(Text(block["tool_use_id"]), block["content"], protocol, doc));
                    break;
                case "thinking": case "redacted_thinking": case "reasoning": case "compaction":
                    throw new LlmConversionException([new("opaque_state", "$content[]." + type, "签名和不透明推理项必须通过原生协议回传。")]);
                default: Loss(doc, "$content[]." + type, "unsupported_content"); break;
            }
        }
    }

    /// <summary>读取 Gemini part，函数缺失 ID 时使用本次转换内稳定的关联 ID。</summary>
    private static void Gemini(JsonObject block, ConversionMessage message, ConversionDocument doc)
    {
        if (block.ContainsKey("thoughtSignature") || block["thought"]?.GetValue<bool>() == true)
            throw new LlmConversionException([new("opaque_state", "$parts[].thoughtSignature", "签名或推理内容必须通过 Gemini 原生协议回传。")]);
        if (new[] { "text", "inlineData", "fileData", "functionCall", "functionResponse" }.Count(block.ContainsKey) != 1)
            throw new ArgumentException("Gemini part 必须包含一种明确内容。");
        Check(block, doc, "$parts[]", "text", "inlineData", "fileData", "functionCall", "functionResponse");
        if (block.ContainsKey("text")) message.Blocks.Add(new() { ["type"] = "text", ["text"] = Text(block["text"]) });
        else if (block["inlineData"] is JsonObject inline)
        {
            if (InputFileMapping.ReadGemini(inline, message, doc)) return;
            Check(inline, doc, "$parts[].inlineData", "mimeType", "data");
            if (!Text(inline["mimeType"]).StartsWith("image/", StringComparison.Ordinal)) { Loss(doc, "$parts[].inlineData", "unsupported_media"); return; }
            message.Blocks.Add(new() { ["type"] = "image", ["url"] = "data:" + Text(inline["mimeType"]) + ";base64," + Text(inline["data"]) });
        }
        else if (block.ContainsKey("fileData")) InputFileMapping.Reference();
        else if (block["functionCall"] is JsonObject call)
        {
            Check(call, doc, "$parts[].functionCall", "id", "name", "args");
            var id = call["id"]?.GetValue<string>();
            if (id is null)
            {
                var sequence = 0;
                do { id = "loki_call_" + sequence++; } while (doc.ReservedCallIds.Contains(id) || doc.GeminiCalls.Contains(id));
            }
            doc.GeminiCalls.Register(id, Text(call["name"]));
            message.Blocks.Add(new() { ["type"] = "call", ["id"] = id, ["name"] = Text(call["name"]), ["arguments"] = Object(call["args"]).ToJsonString() });
        }
        else if (block["functionResponse"] is JsonObject result)
        {
            Check(result, doc, "$parts[].functionResponse", "id", "name", "response");
            var name = Text(result["name"]);
            var id = doc.GeminiCalls.Resolve(result["id"]?.GetValue<string>(), name);
            message.Blocks.Add(new() { ["type"] = "result", ["id"] = id, ["name"] = name, ["output"] = Object(result["response"]).ToJsonString() });
        }
        else Loss(doc, "$parts[]", "unsupported_content");
    }

    /// <summary>先保留显式 Gemini 调用 ID，不改写调用方的身份。</summary>
    internal static void ReserveGeminiIds(JsonArray parts, ConversionDocument doc)
    {
        foreach (var part in parts)
            if (part?["functionCall"]?["id"] is JsonValue id) doc.ReservedCallIds.Add(id.GetValue<string>());
    }

    /// <summary>输出图片块；不下载媒体，也不伪造 MIME 类型。</summary>
    internal static JsonObject Image(JsonObject block, LlmProtocol protocol, ConversionDocument doc)
    {
        var url = Text(block["url"]);
        if (protocol == LlmProtocol.ChatCompletions)
        {
            var image = new JsonObject { ["url"] = url }; Copy(block, "detail", image, "detail");
            return new() { ["type"] = "image_url", ["image_url"] = image };
        }
        if (protocol == LlmProtocol.Responses)
        {
            var image = new JsonObject { ["type"] = "input_image", ["image_url"] = url }; Copy(block, "detail", image, "detail"); return image;
        }
        if (block.ContainsKey("detail")) Loss(doc, "$content[].detail");
        if (!url.StartsWith("data:", StringComparison.Ordinal))
        {
            if (protocol == LlmProtocol.ClaudeMessages) return new() { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "url", ["url"] = url } };
            throw new LlmConversionException([new("media_resolution_required", "$content[].image", "Gemini 图片转换要求明确的内联 MIME 与 Base64 数据；SDK 不自动下载 URL。")]);
        }
        var separator = url.IndexOf(";base64,", StringComparison.Ordinal);
        if (separator < 5) throw new ArgumentException("图片 data URL 必须包含 MIME 与 Base64。");
        var mime = url[5..separator]; var data = url[(separator + 8)..];
        if (!mime.StartsWith("image/", StringComparison.Ordinal)) throw new ArgumentException("图片 MIME 类型无效。");
        _ = Convert.FromBase64String(data);
        return protocol == LlmProtocol.Gemini
            ? new() { ["inlineData"] = new JsonObject { ["mimeType"] = mime, ["data"] = data } }
            : new() { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mime, ["data"] = data } };
    }
}
