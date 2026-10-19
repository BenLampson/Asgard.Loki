using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>识别尚未映射的搜索执行/引用，并对必须原生回放的加密搜索状态硬失败。</summary>
internal static class HostedSearchOutput
{
    /// <summary>只检查原生协议的内容位置，不扫描用户文本、函数参数或 JSON Schema 的任意键。</summary>
    internal static IReadOnlyList<LlmConversionDiagnostic> Inspect(JsonObject value, LlmProtocol protocol, bool mapUrlCitations = false)
    {
        var diagnostics = new List<LlmConversionDiagnostic>();
        if (protocol == LlmProtocol.ClaudeMessages)
        {
            Blocks(value["content"], "$.content"); Blocks(value["system"], "$.system");
            if (value["messages"] is JsonArray messages)
                for (var i = 0; i < messages.Count; i++) Blocks(messages[i]?["content"], "$.messages[" + i + "].content");
            if (value["type"]?.GetValue<string>() == "content_block_start" && value["content_block"] is JsonObject block) Block(block, "$event.content_block");
            if (value["type"]?.GetValue<string>() == "content_block_delta" && value["delta"]?["type"]?.GetValue<string>() == "citations_delta")
                Citation(value["delta"]?["citation"], "$event.delta.citation");
        }
        else if (protocol == LlmProtocol.Responses)
        {
            var eventType = value["type"]?.GetValue<string>();
            if (value["input"] is JsonArray input) for (var i = 0; i < input.Count; i++) ResponseItem(input[i], "$.input[" + i + "]");
            if (value["output"] is JsonArray output) for (var i = 0; i < output.Count; i++) ResponseItem(output[i], "$.output[" + i + "]");
            if (eventType is "response.created" or "response.in_progress" or "response.completed" or "response.incomplete"
                && value["response"]?["output"] is JsonArray finalOutput)
                for (var i = 0; i < finalOutput.Count; i++) ResponseItem(finalOutput[i], "$event.response.output[" + i + "]");
            if (eventType is "response.output_item.added" or "response.output_item.done" && value["item"] is JsonObject item) ResponseItem(item, "$event.item");
            if (eventType is "response.content_part.added" or "response.content_part.done" && value["part"] is JsonObject part) Annotations(part["annotations"], "$event.part.annotations");
            if (eventType == "response.output_text.annotation.added") Annotations(new JsonArray(value["annotation"]?.DeepClone()), "$event.annotation");
        }
        else if (protocol == LlmProtocol.ChatCompletions && value["choices"] is JsonArray choices)
        {
            for (var i = 0; i < choices.Count; i++)
            {
                Annotations(choices[i]?["message"]?["annotations"], "$.choices[" + i + "].message.annotations");
                Annotations(choices[i]?["delta"]?["annotations"], "$.choices[" + i + "].delta.annotations");
            }
        }
        else if (protocol == LlmProtocol.Gemini && value["candidates"] is JsonArray candidates)
            for (var i = 0; i < candidates.Count; i++)
                if (candidates[i] is JsonObject candidate && candidate.ContainsKey("groundingMetadata")) Add("grounding_metadata_not_representable", "$.candidates[" + i + "].groundingMetadata");
        return diagnostics;

        void Blocks(JsonNode? node, string path)
        { if (node is JsonArray blocks) for (var i = 0; i < blocks.Count; i++) if (blocks[i] is JsonObject block) Block(block, path + "[" + i + "]"); }
        void Block(JsonObject block, string path)
        {
            var type = block["type"]?.GetValue<string>();
            if (type == "server_tool_use" && block["name"]?.GetValue<string>() == "web_search") Add("hosted_search_execution_not_representable", path);
            if (type == "web_search_tool_result")
            {
                if (block["content"] is JsonArray results)
                    for (var i = 0; i < results.Count; i++)
                        if (results[i] is JsonObject result && (result["type"]?.GetValue<string>() == "web_search_result" || result.ContainsKey("encrypted_content")))
                            Opaque(path + ".content[" + i + "].encrypted_content");
                if (block["content"] is JsonObject content && content.ContainsKey("encrypted_content")) Opaque(path + ".content.encrypted_content");
                Add(block["content"] is JsonObject error && error["type"]?.GetValue<string>() == "web_search_tool_result_error"
                    ? "hosted_search_error_not_representable" : "hosted_search_result_not_representable", path);
            }
            if (type == "text" && block["citations"] is JsonArray citations)
                for (var i = 0; i < citations.Count; i++) Citation(citations[i], path + ".citations[" + i + "]");
            if (type == "tool_result" && block["content"] is JsonArray toolContent)
                for (var i = 0; i < toolContent.Count; i++)
                    if (toolContent[i] is JsonObject text && text["type"]?.GetValue<string>() == "text" && text["citations"] is JsonArray toolCitations)
                        for (var j = 0; j < toolCitations.Count; j++) Citation(toolCitations[j], path + ".content[" + i + "].citations[" + j + "]");
        }
        void Citation(JsonNode? node, string path)
        {
            if (node is not JsonObject citation) return;
            if (citation.ContainsKey("encrypted_index") || citation["type"]?.GetValue<string>() == "web_search_result_location") Opaque(path + ".encrypted_index");
        }
        void ResponseItem(JsonNode? node, string path)
        {
            if (node is not JsonObject item) return;
            if (item["type"]?.GetValue<string>() == "web_search_call") Add("hosted_search_execution_not_representable", path);
            if (item["content"] is JsonArray content)
                for (var i = 0; i < content.Count; i++) if (content[i] is JsonObject block) Annotations(block["annotations"], path + ".content[" + i + "].annotations");
        }
        void Annotations(JsonNode? node, string path)
        {
            if (!mapUrlCitations && node is JsonArray list && list.OfType<JsonObject>().Any(a => a["type"]?.GetValue<string>() == "url_citation")) Add("search_citations_not_representable", path);
        }
        void Add(string code, string path) => diagnostics.Add(new(code, path, "搜索执行、来源或引用尚不能在目标结构中保真表达。"));
    }

    /// <summary>这些 token 是上游回放状态，不是可经普通损耗许可删去的显示注解。</summary>
    private static void Opaque(string path) => throw new LlmConversionException([new("opaque_state", path, "加密搜索内容或引用索引必须使用原生协议回放。")]);
}
