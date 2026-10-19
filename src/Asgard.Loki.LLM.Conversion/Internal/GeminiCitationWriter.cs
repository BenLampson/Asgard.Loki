using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>将完整 OpenAI URL 引用写为 Gemini Web grounding，不生成搜索执行或合并来源。</summary>
internal static class GeminiCitationWriter
{
    /// <summary>按实际输出 part 索引与显式源单位生成字节范围，保留重复引用及原始文本。</summary>
    internal static JsonObject? Write(ConversionMessage message, ConversionDocument doc)
    {
        var chunks = new JsonArray(); var supports = new JsonArray(); var partIndex = 0;
        foreach (var block in message.Blocks)
        {
            var type = Text(block["type"]);
            if (type is not ("text" or "call")) continue;
            if (type == "text" && block["citations"] is JsonArray citations)
            {
                var text = Text(block["text"]);
                for (var i = 0; i < citations.Count; i++)
                {
                    var citation = Object(citations[i]); var path = "$response.content[" + partIndex + "].annotations[" + i + "]";
                    var url = Text(citation["url"]);
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    { Loss(doc, path + ".url", "grounding_source_not_representable"); continue; }
                    var start = Number(citation["start_index"]); var end = Number(citation["end_index"]);
                    var bytes = CitationOffsets.Convert(text, start, end, doc.CitationIndexUnit, LlmCitationIndexUnit.Utf8Byte);
                    var utf16 = CitationOffsets.Convert(text, start, end, doc.CitationIndexUnit, LlmCitationIndexUnit.Utf16CodeUnit);
                    if (bytes.End > int.MaxValue) throw new InvalidDataException("Gemini segment 字节偏移超出 int32。");
                    var index = chunks.Count;
                    chunks.Add(new JsonObject { ["web"] = new JsonObject { ["uri"] = url, ["title"] = Text(citation["title"]) } });
                    supports.Add(new JsonObject
                    {
                        ["segment"] = new JsonObject { ["partIndex"] = partIndex, ["startIndex"] = bytes.Start, ["endIndex"] = bytes.End,
                            ["text"] = text.Substring((int)utf16.Start, (int)(utf16.End - utf16.Start)) },
                        ["groundingChunkIndices"] = new JsonArray(index)
                    });
                }
            }
            partIndex++;
        }
        return chunks.Count == 0 ? null : new JsonObject { ["groundingChunks"] = chunks, ["groundingSupports"] = supports };
    }
}
