using System.Text.Json;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>将完整 Gemini Web grounding 的明确字节范围映射为 OpenAI URL 引用。</summary>
internal static class GeminiGrounding
{
    private const string Path = "$.candidates[0].groundingMetadata";

    /// <summary>按原始 part 与 chunk 索引关联，不按重复文本定位，也不生成搜索执行 ID。</summary>
    internal static void Read(JsonNode? value, JsonArray parts, ConversionMessage message, ConversionDocument doc)
    {
        var metadata = Object(value);
        foreach (var field in metadata)
            if (field.Key is not ("groundingChunks" or "groundingSupports"))
                Loss(doc, Path + "." + field.Key, "grounding_metadata_field_not_representable");
        var chunks = metadata.ContainsKey("groundingChunks") ? Array(metadata["groundingChunks"]) : new JsonArray();
        var supports = metadata.ContainsKey("groundingSupports") ? Array(metadata["groundingSupports"]) : new JsonArray();
        var sources = new JsonObject?[chunks.Count]; var used = new bool[chunks.Count];
        for (var i = 0; i < chunks.Count; i++) sources[i] = Source(Object(chunks[i]), doc, Path + ".groundingChunks[" + i + "]");
        var textBlocks = message.Blocks.Where(b => Text(b["type"]) == "text").ToArray();
        var byPart = new Dictionary<int, JsonObject>(); var textIndex = 0;
        for (var i = 0; i < parts.Count; i++)
            if (Object(parts[i]).ContainsKey("text")) byPart.Add(i, textBlocks[textIndex++]);
        for (var i = 0; i < supports.Count; i++)
        {
            var support = Object(supports[i]); var supportPath = Path + ".groundingSupports[" + i + "]";
            Check(support, doc, supportPath, "segment", "groundingChunkIndices");
            var segment = Object(support["segment"]);
            Check(segment, doc, supportPath + ".segment", "partIndex", "startIndex", "endIndex", "text");
            var part = Integer(segment, "partIndex");
            if (!byPart.TryGetValue(part, out var block)) throw new InvalidDataException("grounding part 索引必须关联原始文本 part。");
            var text = Text(block["text"]); var start = Integer(segment, "startIndex"); var end = Integer(segment, "endIndex");
            var range = CitationOffsets.Convert(text, start, end, LlmCitationIndexUnit.Utf8Byte, doc.CitationIndexUnit);
            if (segment.ContainsKey("text"))
            {
                var utf16 = CitationOffsets.Convert(text, start, end, LlmCitationIndexUnit.Utf8Byte, LlmCitationIndexUnit.Utf16CodeUnit);
                if (!text.AsSpan((int)utf16.Start, (int)(utf16.End - utf16.Start)).SequenceEqual(Text(segment["text"])))
                    throw new InvalidDataException("grounding segment 文本与原始字节范围不一致。");
            }
            var indices = support.ContainsKey("groundingChunkIndices") ? Array(support["groundingChunkIndices"]) : new JsonArray();
            if (indices.Count == 0) Loss(doc, supportPath, "grounding_support_without_source");
            foreach (var node in indices)
            {
                var index = Integer(node);
                if (index >= sources.Length) throw new InvalidDataException("grounding 来源索引不存在。");
                used[index] = true;
                if (sources[index] is not { } source) continue;
                if (block["citations"] is not JsonArray citations) block["citations"] = citations = new JsonArray();
                citations.Add(new JsonObject { ["type"] = "url_citation", ["url"] = source["url"]!.DeepClone(),
                    ["title"] = source["title"]!.DeepClone(), ["start_index"] = range.Start, ["end_index"] = range.End });
            }
        }
        for (var i = 0; i < used.Length; i++)
            if (!used[i]) Loss(doc, Path + ".groundingChunks[" + i + "]", "grounding_source_without_segment");
    }

    /// <summary>只接受明确的公开 HTTP(S) Web 来源；检索私有上下文和地图来源不冒充 URL 引用。</summary>
    private static JsonObject? Source(JsonObject chunk, ConversionDocument doc, string path)
    {
        if (new[] { "web", "image", "retrievedContext", "maps" }.Count(chunk.ContainsKey) > 1)
            throw new InvalidDataException("grounding 来源必须满足原生 oneof 约束。");
        if (!chunk.ContainsKey("web")) { Loss(doc, path, "grounding_source_not_representable"); return null; }
        Check(chunk, doc, path, "web"); var web = Object(chunk["web"]);
        Check(web, doc, path + ".web", "uri", "title");
        if (!web.ContainsKey("uri") || !web.ContainsKey("title"))
        { Loss(doc, path + ".web", "grounding_source_not_representable"); return null; }
        var uri = Text(web["uri"]); var title = Text(web["title"]);
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
            throw new InvalidDataException("grounding Web URI 必须是绝对 HTTP(S) 地址。");
        return new() { ["url"] = uri, ["title"] = title };
    }

    /// <summary>Proto3 非 optional int32 的省略值为零；显式 null 或非法整数不当作省略。</summary>
    private static int Integer(JsonObject value, string name) => value.ContainsKey(name) ? Integer(value[name]) : 0;

    /// <summary>只接受非负 int32 JSON 数值，不修复错误关联。</summary>
    private static int Integer(JsonNode? value)
    {
        var element = JsonSerializer.SerializeToElement(value);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var index) || index < 0)
            throw new InvalidDataException("grounding 索引必须是非负 int32。");
        return index;
    }
}
