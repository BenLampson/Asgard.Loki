using System.Buffers.Text;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>保留工具结果的字符串/数组种类和内容顺序，不执行工具或隐式构造 Gemini 业务对象。</summary>
internal static class ToolResultMapping
{
    /// <summary>读取一个与原始调用 ID 关联的结果，数组保持为独立的私有内容块。</summary>
    internal static JsonObject Read(string id, JsonNode? output, LlmProtocol protocol, ConversionDocument doc)
    {
        var result = new JsonObject { ["type"] = "result", ["id"] = id };
        if (output is not JsonArray array)
        {
            if (output is not JsonValue scalar || !scalar.TryGetValue<string>(out var text)) throw new ArgumentException("工具结果必须是字符串或原生内容数组。");
            result["output"] = text; return result;
        }
        var parts = new ConversionMessage("user");
        foreach (var node in array)
        {
            var part = Object(node); var type = Text(part["type"]);
            var allowed = protocol switch
            {
                LlmProtocol.ChatCompletions => type == "text",
                LlmProtocol.Responses => type is "input_text" or "input_image" or "input_file",
                LlmProtocol.ClaudeMessages => type is "text" or "image" or "document",
                _ => false
            };
            if (!allowed) Reject("unsupported_tool_result_content", "$tool_result.content[].type");
            if (type == "input_image" && part.ContainsKey("file_id")) InputFileMapping.Reference();
            if (type == "image")
            {
                var source = Object(part["source"]); var sourceType = Text(source["type"]);
                if (sourceType == "file") InputFileMapping.Reference();
                if (sourceType == "base64" && (!Base64.IsValid(Text(source["data"]).AsSpan(), out var length) || length == 0))
                    throw new ArgumentException("工具结果的内联图片必须包含非空标准 Base64。");
            }
            // 工具结果是用户提供的数据；合成的私有角色只用于复用输入内容解析，不产生新用户消息。
            ContentMapping.Read(new JsonArray(part.DeepClone()), protocol, parts, doc);
        }
        result["parts"] = new JsonArray(parts.Blocks.Select(b => (JsonNode)b).ToArray());
        return result;
    }

    /// <summary>显式错误不能删掉标记后变为普通成功结果；false 与原生默认一致。</summary>
    internal static void ClaudeError(JsonObject block)
    {
        if (block.ContainsKey("is_error") && Boolean(block["is_error"]))
            Reject("tool_result_error_not_representable", "$tool_result.is_error");
    }

    /// <summary>目标数组内容逐块转换；不拼接字符串、不 JSON 序列化内容数组以假装业务结果。</summary>
    internal static JsonNode Write(JsonObject result, LlmProtocol protocol, ConversionDocument doc)
    {
        if (result["parts"] is not JsonArray parts) return JsonValue.Create(Text(result["output"]))!;
        if (protocol == LlmProtocol.Gemini) Reject("rich_tool_result_not_representable", "$tool_result.content");
        var output = new JsonArray();
        foreach (var node in parts)
        {
            var block = Object(node); var type = Text(block["type"]);
            if (type == "text") output.Add(new JsonObject { ["type"] = protocol == LlmProtocol.Responses ? "input_text" : "text", ["text"] = Text(block["text"]) });
            else if (protocol == LlmProtocol.ChatCompletions) Reject("rich_tool_result_not_representable", "$tool_result.content[]");
            else if (type == "image") output.Add(ContentMapping.Image(block, protocol, doc));
            else if (type == "file") output.Add(InputFileMapping.Write(block, protocol, doc));
            else Reject("unsupported_tool_result_content", "$tool_result.content[]");
        }
        return output;
    }

    /// <summary>没有等价容器或语义时明确失败，普通元数据损耗许可不执行降级包装。</summary>
    private static void Reject(string code, string path) => throw new LlmConversionException([new(code, path, "工具结果的内容种类或错误语义无法在目标原生结构中等价表达。")]);
}
