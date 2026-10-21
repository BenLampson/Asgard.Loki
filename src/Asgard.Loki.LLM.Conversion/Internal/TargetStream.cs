using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>将即时文本与完整工具块编码为目标原生事件序列。</summary>
internal sealed class TargetStream(LlmProtocol protocol, LlmConversionContext? context = null, bool mapGeminiCitations = false,
    int maximumAnnotations = 1024, int maximumAnnotationBytes = 1024 * 1024, bool concatenateGeminiTextParts = false)
{
    private readonly HashSet<int> _started = [];
    private readonly Dictionary<int, int> _toolIndices = [];
    private string? _id;
    private string _model = "";
    private long _created;
    private int _sequence;
    private int? _activeClaude;
    private readonly HashSet<int> _closedClaude = [];
    private readonly HashSet<int> _outputStarts = [];
    private int _highestGeminiBlock = -1;
    private bool _geminiTextInterleaved;

    /// <summary>发出增量；工具参数在源协议确认完整后发出。</summary>
    internal List<JsonObject> Push(ConversionDocument doc, IEnumerable<StreamChange> changes)
    {
        List<JsonObject> events = [];
        foreach (var change in changes)
        {
            Initialize(doc, events);
            var block = doc.Messages[0].Blocks[change.Index];
            if (!_outputStarts.Contains(change.Index))
            {
                if (change.Index != _outputStarts.Count) throw new InvalidDataException("无法在前序工具尚未完成时输出后续内容；转换不会重排内容块。");
                _outputStarts.Add(change.Index);
            }
            if (protocol == LlmProtocol.ChatCompletions)
            {
                var delta = new JsonObject();
                if (!change.Tool) delta["content"] = change.Delta;
                else
                {
                    if (!_toolIndices.TryGetValue(change.Index, out var toolIndex)) { toolIndex = _toolIndices.Count; _toolIndices.Add(change.Index, toolIndex); }
                    delta["tool_calls"] = new JsonArray(new JsonObject { ["index"] = toolIndex, ["id"] = Text(block["id"]), ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = Text(block["name"]), ["arguments"] = Text(block["arguments"]) } });
                }
                events.Add(Chat(delta));
            }
            else if (protocol == LlmProtocol.Gemini)
            {
                if (concatenateGeminiTextParts && !change.Tool && !string.IsNullOrEmpty(change.Delta)) { _geminiTextInterleaved |= change.Index < _highestGeminiBlock; _highestGeminiBlock = Math.Max(_highestGeminiBlock, change.Index); }
                var part = change.Tool ? new JsonObject { ["functionCall"] = new JsonObject { ["id"] = Text(block["id"]), ["name"] = Text(block["name"]), ["args"] = Object(JsonNode.Parse(Text(block["arguments"]))) } }
                    : new JsonObject { ["text"] = change.Delta };
                events.Add(new() { ["responseId"] = _id, ["modelVersion"] = _model,
                    ["candidates"] = new JsonArray(new JsonObject { ["index"] = 0, ["content"] = new JsonObject { ["role"] = "model", ["parts"] = new JsonArray(part) } }) });
            }
            else if (protocol == LlmProtocol.ClaudeMessages) Claude(change, block, events);
            else Responses(change, block, events);
        }
        return events;
    }

    /// <summary>仅在源流已确认结束时发出最终 usage 与终态。</summary>
    internal List<JsonObject> Complete(ConversionDocument doc)
    {
        List<JsonObject> events = [];
        Initialize(doc, events);
        doc.Id = _id!;
        if (mapGeminiCitations) GeminiCitationStreamTarget.Prepare(doc, concatenateGeminiTextParts, _geminiTextInterleaved);
        var complete = ResponseMapping.Write(doc, protocol, mapGeminiCitations);
        if (mapGeminiCitations) GeminiCitationStreamTarget.RequireLimits(complete, maximumAnnotations, maximumAnnotationBytes);
        if (protocol == LlmProtocol.ChatCompletions)
        {
            var last = Chat(new()); last["choices"]![0]!["finish_reason"] = doc.Finish;
            last["usage"] = complete["usage"]?.DeepClone(); events.Add(last);
        }
        else if (protocol == LlmProtocol.Gemini)
        {
            complete["candidates"]![0]!["content"] = new JsonObject { ["role"] = "model", ["parts"] = new JsonArray() };
            events.Add(complete);
        }
        else if (protocol == LlmProtocol.ClaudeMessages)
        {
            CloseClaude(events);
            events.Add(new() { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = complete["stop_reason"]!.DeepClone(), ["stop_sequence"] = null }, ["usage"] = complete["usage"]!.DeepClone() });
            events.Add(new() { ["type"] = "message_stop" });
        }
        else
        {
            var output = new JsonArray();
            for (var i = 0; i < doc.Messages[0].Blocks.Count; i++)
            {
                var block = doc.Messages[0].Blocks[i]; var item = ResponseItem(block, i, true);
                if (doc.Finish is "length" or "content_filter") item["status"] = "incomplete";
                if (Text(block["type"]) == "text")
                {
                    var annotations = item["content"]![0]!["annotations"]!.AsArray();
                    for (var annotationIndex = 0; annotationIndex < annotations.Count; annotationIndex++)
                        events.Add(Event("response.output_text.annotation.added", i, new() { ["content_index"] = 0, ["item_id"] = ItemId(i),
                            ["annotation_index"] = annotationIndex, ["annotation"] = annotations[annotationIndex]!.DeepClone() }));
                    events.Add(Event("response.output_text.done", i, new() { ["content_index"] = 0, ["text"] = Text(block["text"]), ["item_id"] = ItemId(i) }));
                    events.Add(Event("response.content_part.done", i, new() { ["content_index"] = 0, ["part"] = item["content"]![0]!.DeepClone(), ["item_id"] = ItemId(i) }));
                }
                else if (Text(item["type"]) == "custom_tool_call")
                    events.Add(Event("response.custom_tool_call_input.done", i, new() { ["input"] = Text(item["input"]), ["item_id"] = ItemId(i) }));
                else events.Add(Event("response.function_call_arguments.done", i, new() { ["arguments"] = Text(block["arguments"]), ["item_id"] = ItemId(i) }));
                events.Add(Event("response.output_item.done", i, new() { ["item"] = item.DeepClone() })); output.Add(item);
            }
            complete["output"] = output;
            var end = new JsonObject { ["type"] = doc.Finish is "length" or "content_filter" ? "response.incomplete" : "response.completed", ["sequence_number"] = _sequence++, ["response"] = complete };
            events.Add(end);
        }
        return events;
    }

    /// <summary>为一条转换流创建稳定身份及目标协议开始事件。</summary>
    private void Initialize(ConversionDocument doc, List<JsonObject> events)
    {
        if (_id is not null) return;
        _id = doc.Id.Length != 0 ? doc.Id : "loki_" + Guid.NewGuid().ToString("N"); _model = doc.Model; _created = doc.Created;
        if (protocol == LlmProtocol.ChatCompletions) events.Add(Chat(new() { ["role"] = "assistant", ["content"] = "" }));
        else if (protocol == LlmProtocol.ClaudeMessages)
        {
            var initialUsage = UsageMapping.Write(doc, protocol, requireComplete: false);
            if (!initialUsage.ContainsKey("input_tokens")) initialUsage["input_tokens"] = 0;
            initialUsage["output_tokens"] = 0;
            events.Add(new() { ["type"] = "message_start", ["message"] = new JsonObject { ["id"] = _id, ["type"] = "message", ["role"] = "assistant", ["model"] = _model,
                ["content"] = new JsonArray(), ["stop_reason"] = null, ["stop_sequence"] = null, ["usage"] = initialUsage } });
        }
        else if (protocol == LlmProtocol.Responses)
        {
            foreach (var type in new[] { "response.created", "response.in_progress" })
                events.Add(new() { ["type"] = type, ["sequence_number"] = _sequence++, ["response"] = new JsonObject { ["id"] = _id, ["object"] = "response", ["created_at"] = _created, ["model"] = _model,
                    ["status"] = "in_progress", ["output"] = new JsonArray() } });
        }
    }

    /// <summary>Claude 内容块保持顺序，拒绝向已关闭块继续追加内容。</summary>
    private void Claude(StreamChange change, JsonObject block, List<JsonObject> events)
    {
        if (_closedClaude.Contains(change.Index)) throw new InvalidDataException("无法将交错内容增量转换为顺序 Claude 内容块。");
        if (_activeClaude != change.Index)
        {
            CloseClaude(events); _activeClaude = change.Index;
            var content = change.Tool ? new JsonObject { ["type"] = "tool_use", ["id"] = Text(block["id"]), ["name"] = Text(block["name"]), ["input"] = new JsonObject() }
                : new JsonObject { ["type"] = "text", ["text"] = "" };
            events.Add(new() { ["type"] = "content_block_start", ["index"] = change.Index, ["content_block"] = content });
        }
        events.Add(new() { ["type"] = "content_block_delta", ["index"] = change.Index, ["delta"] = change.Tool
            ? new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = Text(block["arguments"]) }
            : new JsonObject { ["type"] = "text_delta", ["text"] = change.Delta } });
    }
    /// <summary>终止一个 Claude 内容块，避免重复结束。</summary>
    private void CloseClaude(List<JsonObject> events)
    {
        if (_activeClaude is not { } index) return;
        events.Add(new() { ["type"] = "content_block_stop", ["index"] = index }); _closedClaude.Add(index); _activeClaude = null;
    }

    /// <summary>Responses 同时维护 item ID、output_index、content_index 和 sequence_number。</summary>
    private void Responses(StreamChange change, JsonObject block, List<JsonObject> events)
    {
        var index = change.Index;
        if (_started.Add(index))
        {
            events.Add(Event("response.output_item.added", index, new() { ["item"] = ResponseItem(block, index, false) }));
            if (!change.Tool) events.Add(Event("response.content_part.added", index, new() { ["item_id"] = ItemId(index), ["content_index"] = 0,
                ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = "", ["annotations"] = new JsonArray() } }));
        }
        var custom = change.Tool && context?.ResolveTool(Text(block["name"]), Text(block["id"]))?.Identity.IsCustom == true;
        var delta = custom ? CustomToolTextCodec.Decode(Text(block["arguments"]), context!.Options.MaxCustomToolInputBytes)
            : change.Tool ? Text(block["arguments"]) : change.Delta;
        var extra = new JsonObject { ["item_id"] = ItemId(index), ["delta"] = delta };
        if (!change.Tool) extra["content_index"] = 0;
        events.Add(Event(custom ? "response.custom_tool_call_input.delta" : change.Tool ? "response.function_call_arguments.delta" : "response.output_text.delta", index, extra));
    }

    /// <summary>构造单个 Responses 输出项，最终快照与流内项目完全一致。</summary>
    private JsonObject ResponseItem(JsonObject block, int index, bool complete)
    {
        if (Text(block["type"]) == "call")
        {
            var item = new JsonObject { ["id"] = ItemId(index), ["type"] = "function_call", ["call_id"] = Text(block["id"]),
                ["name"] = Text(block["name"]), ["arguments"] = complete ? Text(block["arguments"]) : "", ["status"] = complete ? "completed" : "in_progress" };
            context?.RestoreItem(item, complete); return item;
        }
        return new() { ["id"] = ItemId(index), ["type"] = "message", ["role"] = "assistant", ["status"] = complete ? "completed" : "in_progress", ["content"] = complete
            ? new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = Text(block["text"]), ["annotations"] = block["citations"]?.DeepClone() ?? new JsonArray() }) : new JsonArray() };
    }
    /// <summary>稳定的目标输出项标识。</summary>
    private string ItemId(int index) => "item_" + _id + "_" + index;
    /// <summary>构造具有递增序号的 Responses 事件。</summary>
    private JsonObject Event(string type, int index, JsonObject extra)
    {
        extra["type"] = type; extra["output_index"] = index; extra["sequence_number"] = _sequence++; return extra;
    }
    /// <summary>构造单候选 Chat 增量。</summary>
    private JsonObject Chat(JsonObject delta) => new() { ["id"] = _id, ["object"] = "chat.completion.chunk", ["created"] = _created, ["model"] = _model,
        ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = null }) };
}
