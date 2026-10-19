using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>验证 Responses 各输出项的身份、增量、完成快照与终态。</summary>
internal sealed partial class SourceStream
{
    private bool _responsesStarted;
    private readonly Dictionary<int, string> _responseItemIds = [];
    private readonly Dictionary<int, string> _responseItemTypes = [];
    private readonly HashSet<int> _responseItemsDone = [];
    private readonly HashSet<string> _responseTextDone = new(StringComparer.Ordinal);
    private readonly HashSet<string> _responsePartsDone = new(StringComparer.Ordinal);
    private long? _lastSequence;

    /// <summary>保留事件边界；done 和最终快照必须与已经转换的数据一致。</summary>
    private void Responses(JsonObject data, List<StreamChange> changes)
    {
        var type = Text(data["type"]);
        Check(data, Document, "$event", type switch
        {
            "response.created" or "response.in_progress" or "response.completed" or "response.incomplete" => ["type", "sequence_number", "response"],
            "response.output_item.added" or "response.output_item.done" => ["type", "sequence_number", "output_index", "item"],
            "response.content_part.added" or "response.content_part.done" => ["type", "sequence_number", "output_index", "content_index", "item_id", "part"],
            "response.output_text.delta" => ["type", "sequence_number", "output_index", "content_index", "item_id", "delta", "logprobs"],
            "response.output_text.done" => ["type", "sequence_number", "output_index", "content_index", "item_id", "text", "logprobs"],
            "response.output_text.annotation.added" => ["type", "sequence_number", "output_index", "content_index", "item_id", "annotation_index", "annotation"],
            "response.function_call_arguments.delta" => ["type", "sequence_number", "output_index", "item_id", "delta"],
            "response.function_call_arguments.done" => ["type", "sequence_number", "output_index", "item_id", "arguments", "name"],
            _ => ["type", "sequence_number"]
        });
        if (data.ContainsKey("sequence_number"))
        {
            var sequence = Number(data["sequence_number"]);
            if (sequence < 0 || _lastSequence is { } previous && sequence <= previous) throw new InvalidDataException("Responses 事件序号必须递增。");
            _lastSequence = sequence;
        }
        if (type is "response.created" or "response.in_progress")
        {
            if (type == "response.created" && _responsesStarted) throw new InvalidDataException("Responses 重复开始。");
            var response = ResponseEnvelope.Content(Object(data["response"]), LlmProtocol.Responses, Document);
            Check(response, Document, "$event.response", "id", "object", "created_at", "model", "status", "output", "usage", "error", "incomplete_details");
            if (response["error"] is not null) throw new InvalidDataException("Responses 开始事件包含上游错误。");
            if (response["status"] is not null && Text(response["status"]) is not ("in_progress" or "queued"))
                throw new InvalidDataException("Responses 开始事件状态无效。");
            if (response["output"] is JsonArray initial && initial.Count != 0) throw new InvalidDataException("Responses 开始事件已有未处理的输出。");
            UsageMapping.Read(response["usage"], LlmProtocol.Responses, Document);
            Identity(response, "id", "model"); _responsesStarted = true; return;
        }
        if (type is "response.completed" or "response.incomplete") { FinishResponses(data, type); return; }
        if (type is not ("response.output_item.added" or "response.output_item.done" or "response.content_part.added" or "response.content_part.done"
            or "response.output_text.delta" or "response.output_text.done" or "response.output_text.annotation.added" or "response.function_call_arguments.delta" or "response.function_call_arguments.done"))
        { Loss(Document, "$event." + type, "unknown_event"); return; }
        if (!_responsesStarted) throw new InvalidDataException("Responses 内容事件之前缺少开始事件。");
        var index = Index(data, "output_index"); var key = "responses-" + index;
        if (type == "response.output_item.added")
        {
            var item = Object(data["item"]); var itemType = Text(item["type"]); var itemId = Text(item["id"]);
            if (!_responseItemIds.TryAdd(index, itemId) || _responseItemIds.Values.Count(v => v == itemId) != 1)
                throw new InvalidDataException("Responses 输出索引或 item ID 重复。");
            _responseItemTypes.Add(index, itemType);
            if (itemType == "function_call")
            {
                Check(item, Document, "$event.item", "id", "type", "status", "call_id", "name", "arguments");
                var blockIndex = Block(key, "call"); var block = _message.Blocks[blockIndex];
                block["id"] = Text(item["call_id"]); block["name"] = Text(item["name"]);
                SetArguments(blockIndex, item["arguments"]?.GetValue<string>() ?? "");
            }
            else if (itemType == "message")
            {
                Check(item, Document, "$event.item", "id", "type", "status", "role", "content");
                if (Text(item["role"]) != "assistant" || Array(item["content"]).Count != 0) throw new InvalidDataException("Responses message 开始结构无效。");
            }
            else if (itemType is "reasoning" or "compaction") throw new LlmConversionException([new("opaque_state", "$event.item." + itemType, "不透明推理或压缩项必须使用原生协议。")]);
            else Loss(Document, "$event.item." + itemType, "unsupported_item");
            return;
        }
        if (!_responseItemIds.TryGetValue(index, out var expectedId) || _responseItemsDone.Contains(index))
            throw new InvalidDataException("Responses 事件引用未开始或已完成的输出项。");
        if (data.ContainsKey("item_id") && Text(data["item_id"]) != expectedId) throw new InvalidDataException("Responses item_id 与 output_index 不匹配。");
        if (type == "response.output_item.done") { FinishResponseItem(index, Object(data["item"]), changes); return; }
        if (type.StartsWith("response.function_call_arguments.", StringComparison.Ordinal))
        {
            if (_responseItemTypes[index] != "function_call") throw new InvalidDataException("函数参数增量引用非函数输出项。");
            var blockIndex = Existing(key); var call = _message.Blocks[blockIndex];
            if (type.EndsWith(".delta", StringComparison.Ordinal)) AppendArguments(blockIndex, data["delta"]);
            else
            {
                if (_emittedTools.Contains(blockIndex)) throw new InvalidDataException("函数参数重复完成。");
                if (Text(call["arguments"]) != Text(data["arguments"]) || data["name"] is not null && Text(data["name"]) != Text(call["name"]))
                    throw new InvalidDataException("工具参数或函数名称与最终值不一致。");
                CompleteTool(blockIndex, changes);
            }
            return;
        }
        if (_responseItemTypes[index] != "message") throw new InvalidDataException("文本事件引用非消息输出项。");
        var textKey = key + "-text-" + Index(data, "content_index");
        if (_responsePartsDone.Contains(textKey)) throw new InvalidDataException("已完成的内容块不能继续接收事件。");
        if (type == "response.output_text.annotation.added")
        {
            _ = Existing(textKey);
            if (!data.ContainsKey("item_id") || !data.ContainsKey("annotation")) throw new InvalidDataException("原生引用事件缺少 item_id 或 annotation。");
            _responseAnnotations.Add(textKey, Index(data, "annotation_index"), data["annotation"], Document); return;
        }
        if (data["logprobs"] is not null && Array(data["logprobs"]).Count != 0) Loss(Document, "$event.logprobs");
        switch (type)
        {
            case "response.content_part.added":
                if (_indices.ContainsKey(textKey)) throw new InvalidDataException("Responses 内容块重复开始。");
                ReadResponsePart(Object(data["part"]), textKey, changes, true); break;
            case "response.output_text.delta":
                if (_responseTextDone.Contains(textKey) || _responsePartsDone.Contains(textKey)) throw new InvalidDataException("已完成的文本不能继续追加。");
                TextDelta(textKey, Text(data["delta"]), changes); break;
            case "response.output_text.done":
                if (!_responseTextDone.Add(textKey)) throw new InvalidDataException("文本重复完成。");
                if (Text(_message.Blocks[Existing(textKey)]["text"]) != Text(data["text"])) throw new InvalidDataException("文本 done 与增量不一致。");
                break;
            case "response.content_part.done":
                if (!_responsePartsDone.Add(textKey)) throw new InvalidDataException("内容块重复完成。");
                ReadResponsePart(Object(data["part"]), textKey, changes, false); break;
            default: Loss(Document, "$event." + type, "unknown_event"); break;
        }
    }

    /// <summary>验证内容块的全部已知和未知字段，不忽略 done 快照。</summary>
    private void ReadResponsePart(JsonObject part, string key, List<StreamChange> changes, bool start)
    {
        Check(part, Document, "$event.part", "type", "text", "annotations");
        if (Text(part["type"]) != "output_text") { Loss(Document, "$event.part", "unsupported_content"); return; }
        if (part.ContainsKey("annotations"))
        {
            if (part["annotations"] is not JsonArray annotations) throw new InvalidDataException("原生 annotations 必须是数组。");
            if (annotations.Count != 0) Loss(Document, "$event.part.annotations");
        }
        if (start) { TextDelta(key, Text(part["text"]), changes); _responseAnnotations.Start(key, part, Document); }
        else
        {
            if (Text(_message.Blocks[Existing(key)]["text"]) != Text(part["text"])) throw new InvalidDataException("内容块 done 与增量不一致。");
            _responseAnnotations.Snapshot(key, part, Document);
        }
    }

    /// <summary>输出项的原始 ID、名称、内容和工具参数必须与此前事件一致。</summary>
    private void FinishResponseItem(int index, JsonObject item, List<StreamChange> changes)
    {
        if (Text(item["id"]) != _responseItemIds[index] || Text(item["type"]) != _responseItemTypes[index])
            throw new InvalidDataException("Responses 输出项完成时改变身份或类型。");
        _responseAnnotations.Item(index, item, Document);
        var read = ResponseMapping.Read(new() { ["id"] = Document.Id, ["model"] = Document.Model, ["status"] = "completed", ["output"] = new JsonArray(item.DeepClone()) }, LlmProtocol.Responses, mapCitations: false);
        Document.Diagnostics.AddRange(read.Diagnostics);
        var prefix = "responses-" + index;
        var actual = _indices.Where(p => p.Key == prefix || p.Key.StartsWith(prefix + "-text-", StringComparison.Ordinal)).OrderBy(p => p.Value).Select(p => _message.Blocks[p.Value]).ToArray();
        if (!JsonNode.DeepEquals(System.Text.Json.JsonSerializer.SerializeToNode(read.Messages[0].Blocks), System.Text.Json.JsonSerializer.SerializeToNode(actual)))
            throw new InvalidDataException("Responses 输出项 done 与增量不一致。");
        _responseItemsDone.Add(index);
        if (_responseItemTypes[index] == "function_call") CompleteTool(Existing(prefix), changes);
    }

    /// <summary>最终状态、原始项目身份和完整输出都必须与流内数据一致。</summary>
    private void FinishResponses(JsonObject data, string type)
    {
        if (!_responsesStarted) throw new InvalidDataException("Responses 缺少开始事件。");
        var response = Object(data["response"]);
        if (Text(response["status"]) != (type == "response.completed" ? "completed" : "incomplete")) throw new InvalidDataException("Responses 终态事件名与状态不一致。");
        var output = Array(response["output"]);
        if (output.Count != _responseItemIds.Count) throw new InvalidDataException("Responses 最终输出项目数量不一致。");
        for (var i = 0; i < output.Count; i++)
            if (!_responseItemIds.TryGetValue(i, out var id) || Text(Object(output[i])["id"]) != id) throw new InvalidDataException("Responses 最终项目 ID 或索引不一致。");
        for (var i = 0; i < output.Count; i++) _responseAnnotations.Item(i, Object(output[i]), Document);
        var complete = ResponseMapping.Read(response, LlmProtocol.Responses, mapCitations: false); Document.Diagnostics.AddRange(complete.Diagnostics);
        if (!JsonNode.DeepEquals(System.Text.Json.JsonSerializer.SerializeToNode(complete.Messages[0].Blocks), System.Text.Json.JsonSerializer.SerializeToNode(_message.Blocks)))
            throw new InvalidDataException("Responses 最终输出与已转换的增量不一致。");
        Document.Finish = complete.Finish;
        Document.Usage.Clear();
        foreach (var usage in complete.Usage) Document.Usage[usage.Key] = usage.Value?.DeepClone();
        Identity(response, "id", "model"); _terminal = true;
    }

    /// <summary>索引必须显式存在且是非负整数。</summary>
    private static int Index(JsonObject data, string name)
    {
        var value = System.Text.Json.JsonSerializer.SerializeToElement(data[name]);
        if (!data.ContainsKey(name) || value.ValueKind != System.Text.Json.JsonValueKind.Number || !value.TryGetInt32(out var index) || index < 0)
            throw new InvalidDataException("Responses 事件索引无效。");
        return index;
    }
}
