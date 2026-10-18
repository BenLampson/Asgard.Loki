using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>独立维护原生流的协议状态和完整性校验。</summary>
internal sealed partial class SourceStream
{
    /// <summary>Gemini 函数调用已包含完整 args；文本 part 即时转换。</summary>
    private void Gemini(JsonObject data, List<StreamChange> changes)
    {
        Check(data, Document, "$event", "responseId", "modelVersion", "candidates", "usageMetadata"); Identity(data, "responseId", "modelVersion");
        UsageMapping.Read(data["usageMetadata"], protocol, Document);
        if (data["candidates"] is not JsonArray candidates) return;
        if (candidates.Count > 1) throw new NotSupportedException("流式转换只支持一个候选。");
        foreach (var node in candidates)
        {
            var candidate = Object(node); Check(candidate, Document, "$event.candidates[]", "index", "content", "finishReason");
            if ((candidate["index"]?.GetValue<int>() ?? 0) != 0) throw new NotSupportedException("流式转换只支持一个候选。");
            if (candidate["content"] is JsonObject content)
            {
                Check(content, Document, "$event.content", "role", "parts");
                if (content["role"] is not null && Text(content["role"]) != "model") throw new InvalidDataException("Gemini 生成流角色必须为 model。");
                foreach (var part in Array(content["parts"]))
                {
                    var p = Object(part);
                    var temporary = new ConversionMessage("assistant"); ContentMapping.Read(new JsonArray(p.DeepClone()), protocol, temporary, Document);
                    foreach (var block in temporary.Blocks)
                    {
                        if (Text(block["type"]) == "text") { TextDelta("gemini-text-" + _emittedTools.Count, Text(block["text"]), changes); continue; }
                        if (Text(block["type"]) != "call") { Loss(Document, "$event.parts[]", "unsupported_content"); continue; }
                        var i = Block("gemini-tool-" + _emittedTools.Count, "call");
                        foreach (var field in block) _message.Blocks[i][field.Key] = field.Value?.DeepClone();
                        CompleteTool(i, changes);
                    }
                }
            }
            if (candidate["finishReason"] is not null) { Document.Finish = ResponseMapping.Finish(Text(candidate["finishReason"]), protocol); _terminal = true; }
        }
    }

}
