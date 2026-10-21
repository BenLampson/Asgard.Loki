using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>独立维护原生流的协议状态和完整性校验。</summary>
internal sealed partial class SourceStream
{
    private int _chatTextSegment;
    /// <summary>Chat 允许结束块之后继续发送独立 usage 块。</summary>
    private void Chat(JsonObject data, List<StreamChange> changes)
    {
        data = ResponseEnvelope.Content(data, protocol, Document);
        Check(data, Document, "$event", "id", "object", "created", "model", "choices", "usage");
        Identity(data, "id", "model");
        UsageMapping.Read(data["usage"], protocol, Document);
        var choices = Array(data["choices"]);
        if (choices.Count > 1) throw new NotSupportedException("流式转换只支持一个候选。");
        foreach (var node in choices)
        {
            var choice = Object(node);
            Check(choice, Document, "$event.choices[]", "index", "delta", "finish_reason", "logprobs");
            if (choice["logprobs"] is not null) Loss(Document, "$event.choices[].logprobs");
            if (choice["index"]?.GetValue<int>() != 0) throw new NotSupportedException("流式转换只支持一个候选。");
            var delta = Object(choice["delta"]);
            delta = delta.DeepClone().AsObject();
            if (delta.ContainsKey("refusal") && delta["refusal"] is null) delta.Remove("refusal");
            Check(delta, Document, "$event.delta", "role", "content", "tool_calls");
            if (delta["role"] is not null && Text(delta["role"]) != "assistant") throw new InvalidDataException("Chat 流角色必须为 assistant。");
            if (delta["content"] is not null) TextDelta("chat-text-" + _chatTextSegment, Text(delta["content"]), changes);
            if (delta["tool_calls"] is JsonArray calls)
                foreach (var nodeCall in calls)
                {
                    var call = Object(nodeCall);
                    Check(call, Document, "$event.tool_calls[]", "index", "id", "type", "function");
                    if (call["type"] is not null && Text(call["type"]) != "function") Loss(Document, "$event.tool_calls[].type");
                    var toolKey = "chat-tool-" + call["index"]!.GetValue<int>();
                    if (Number(call["index"]) < 0) throw new InvalidDataException("工具索引不能为负数。");
                    if (!_indices.ContainsKey(toolKey)) _chatTextSegment++;
                    var index = Block(toolKey, "call");
                    var block = _message.Blocks[index];
                    if (block["id"] is not null && call["id"] is not null && Text(block["id"]) != Text(call["id"]))
                        throw new InvalidDataException("工具参数增量改变了原始调用 ID。");
                    Copy(call, "id", block, "id");
                    if (call["function"] is JsonObject function)
                    {
                        Check(function, Document, "$event.function", "name", "arguments");
                        Append(block, "name", function["name"]); AppendArguments(index, function["arguments"]);
                    }
                }
            if (choice["finish_reason"] is not null) { Document.Finish = ResponseMapping.Finish(Text(choice["finish_reason"]), protocol); _terminal = true; }
        }
    }

}
