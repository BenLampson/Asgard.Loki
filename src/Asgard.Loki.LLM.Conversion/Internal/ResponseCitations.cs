using System.Text;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>在完整 OpenAI 响应之间保留 URL 引用；不推断 Unicode 索引单位或生成回放状态。</summary>
internal static class ResponseCitations
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>归一化引用外壳，保留原 URL、标题和整数范围，不抓取或改写引用内容。</summary>
    internal static JsonArray Read(JsonNode? value, string text, LlmProtocol protocol, ConversionDocument doc, string path)
    {
        var result = new JsonArray(); var annotations = Array(value); var maximumLength = -1;
        for (var i = 0; i < annotations.Count; i++)
        {
            var annotation = Object(annotations[i]); var itemPath = path + "[" + i + "]";
            if (Text(annotation["type"]) != "url_citation") { Loss(doc, itemPath, "annotation_not_representable"); continue; }
            var citation = annotation;
            if (protocol == LlmProtocol.ChatCompletions)
            {
                Check(annotation, doc, itemPath, "type", "url_citation");
                citation = Object(annotation["url_citation"]);
                Check(citation, doc, itemPath + ".url_citation", "url", "title", "start_index", "end_index");
            }
            else Check(citation, doc, itemPath, "type", "url", "title", "start_index", "end_index");
            var start = Index(citation["start_index"]); var end = Index(citation["end_index"]);
            // 文档只规定 character index；UTF-8 长度是常见索引单位的上界，不宣称其就是原生单位。
            if (maximumLength < 0) maximumLength = StrictUtf8.GetByteCount(text);
            if (end < start || end > maximumLength) throw new InvalidDataException("URL 引用范围无效。");
            result.Add(new JsonObject { ["type"] = "url_citation", ["url"] = Text(citation["url"]), ["title"] = Text(citation["title"]),
                ["start_index"] = start, ["end_index"] = end });
        }
        return result;
    }

    /// <summary>将各文本块范围平移到 Chat 的整段文本；不明确的 Unicode 前缀必须诊断。</summary>
    internal static JsonArray Chat(ConversionMessage message, ConversionDocument doc)
    {
        var result = new JsonArray(); long offset = 0; var asciiPrefix = true;
        foreach (var block in message.Blocks.Where(b => Text(b["type"]) == "text"))
        {
            if (block["citations"] is JsonArray citations && citations.Count != 0)
            {
                if (!asciiPrefix) Loss(doc, "$response.content[].annotations", "citation_offset_unit_unknown");
                else foreach (var node in citations)
                {
                    var source = Object(node);
                    result.Add(new JsonObject { ["type"] = "url_citation", ["url_citation"] = new JsonObject {
                        ["url"] = Text(source["url"]), ["title"] = Text(source["title"]),
                        ["start_index"] = checked(Number(source["start_index"]) + offset), ["end_index"] = checked(Number(source["end_index"]) + offset) } });
                }
            }
            var text = Text(block["text"]); asciiPrefix &= text.All(char.IsAscii); offset = checked(offset + text.Length);
        }
        return result;
    }

    /// <summary>没有对应原生字段的目标必须明确报告损耗，不能把引用伪装成文本或工具结果。</summary>
    internal static void CheckTarget(JsonObject block, LlmProtocol protocol, ConversionDocument doc)
    {
        if (protocol is not (LlmProtocol.ChatCompletions or LlmProtocol.Responses) && block["citations"] is JsonArray { Count: > 0 })
            Loss(doc, "$response.content[].annotations", "search_citations_not_representable");
    }

    /// <summary>缺失、负数、小数及非数值索引都是无效协议值，普通损耗许可不修复它们。</summary>
    private static long Index(JsonNode? value)
    {
        var element = System.Text.Json.JsonSerializer.SerializeToElement(value);
        if (element.ValueKind != System.Text.Json.JsonValueKind.Number || !element.TryGetInt64(out var index) || index < 0)
            throw new InvalidDataException("URL 引用索引必须是非负整数。");
        return index;
    }
}
