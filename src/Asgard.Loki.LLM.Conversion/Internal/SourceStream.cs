using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>聚合单条原生流的文本、函数参数与终态，不恢复或重试网络连接。</summary>
internal sealed partial class SourceStream(LlmProtocol protocol, long? maximumArgumentBytes = null, int? maximumToolCalls = null)
{
    private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);
    private readonly HashSet<int> _emittedTools = [];
    private readonly ConversionMessage _message = new("assistant");
    private bool _started;
    private bool _terminal;
    private readonly ToolCallTracker _toolIds = new();
    private readonly Dictionary<int, long> _argumentSizes = [];
    private int _toolCount;
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>跨事件维护的单次转换文档。</summary>
    internal ConversionDocument Document { get; } = new();

    /// <summary>读取一个原生事件，文本立即产生增量，函数参数在完整时产生调用。</summary>
    internal List<StreamChange> Push(JsonObject data)
    {
        if (!_started) { Document.Messages.Add(_message); _started = true; }
        var changes = new List<StreamChange>();
        if (data["error"] is not null || data["type"]?.GetValue<string>() is "error" or "response.failed")
            throw new LlmConversionException([new("upstream_stream_error", "$event.error", "上游错误事件不能转换为成功终态。")]);
        if (_terminal && !IsUsageTail(data))
            throw new InvalidDataException("源流终态之后只允许独立用量尾块，不能重复结束或追加内容。");
        switch (protocol)
        {
            case LlmProtocol.ChatCompletions: Chat(data, changes); break;
            case LlmProtocol.Responses: Responses(data, changes); break;
            case LlmProtocol.ClaudeMessages: Claude(data, changes); break;
            case LlmProtocol.Gemini: Gemini(data, changes); break;
        }
        return changes;
    }

    /// <summary>允许供应商在明确终态后发送独立用量，不允许隐藏新的候选或错误终态。</summary>
    private bool IsUsageTail(JsonObject data)
    {
        if (protocol is not (LlmProtocol.ChatCompletions or LlmProtocol.Gemini)) return false;
        var candidates = protocol == LlmProtocol.ChatCompletions ? "choices" : "candidates";
        return data[protocol == LlmProtocol.ChatCompletions ? "usage" : "usageMetadata"] is JsonObject
            && (protocol == LlmProtocol.Gemini && !data.ContainsKey(candidates) || data[candidates] is JsonArray values && values.Count == 0);
    }

    /// <summary>只有明确的协议终态才允许产生目标协议结束事件。</summary>
    internal List<StreamChange> Complete()
    {
        if (!_terminal) throw new InvalidDataException("源流缺少协议终态，转换不会伪造成功。");
        List<StreamChange> changes = [];
        for (var i = 0; i < _message.Blocks.Count; i++)
            if (Text(_message.Blocks[i]["type"]) == "call") CompleteTool(i, changes);
        var ids = _message.Blocks.Where(b => Text(b["type"]) == "call").Select(b => Text(b["id"])).ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new InvalidDataException("流中工具关联 ID 为空或重复。");
        if (protocol is LlmProtocol.Responses or LlmProtocol.Gemini && Document.Finish == "stop"
            && _message.Blocks.Any(b => Text(b["type"]) == "call")) Document.Finish = "tool_calls";
        ResponseMapping.ValidateFinish(Document);
        return changes;
    }

    /// <summary>验证稳定响应身份，避免把两个响应混成同一条流。</summary>
    private void Identity(JsonObject data, string id, string model)
    {
        if (data[protocol == LlmProtocol.ChatCompletions ? "created" : "created_at"] is JsonValue timestamp)
            Document.Created = Number(timestamp);
        if (data[id] is not null)
        {
            var value = Text(data[id]);
            if (Document.Id.Length != 0 && Document.Id != value) throw new InvalidDataException("流中响应 ID 发生变化。");
            Document.Id = value;
        }
        if (data[model] is not null)
        {
            var value = Text(data[model]);
            if (Document.Model.Length != 0 && Document.Model != value) throw new InvalidDataException("流中的模型身份发生变化。");
            Document.Model = value;
        }
    }
    /// <summary>建立稳定内容索引并拒绝终态后新增内容。</summary>
    private int Block(string key, string type)
    {
        if (_terminal) throw new InvalidDataException("源流终态后仍有内容。");
        if (_indices.TryGetValue(key, out var index))
        {
            if (Text(_message.Blocks[index]["type"]) != type) throw new InvalidDataException("内容块在流中改变类型。");
            return index;
        }
        if (type == "call" && maximumToolCalls is { } maximum && ++_toolCount > maximum)
            ToolRequestCodec.Reject("$event.tool_calls", "custom_tool_output_limit");
        index = _message.Blocks.Count; _indices.Add(key, index);
        _message.Blocks.Add(new() { ["type"] = type, [type == "text" ? "text" : "arguments"] = "" });
        return index;
    }
    /// <summary>增量必须引用已有函数调用。</summary>
    private int Existing(string key) => _indices.TryGetValue(key, out var index) ? index : throw new InvalidDataException("增量引用了未开始的内容块。");
    /// <summary>聚合文本并即时返回增量。</summary>
    private void TextDelta(string key, string delta, List<StreamChange> changes)
    {
        var index = Block(key, "text"); Append(_message.Blocks[index], "text", JsonValue.Create(delta)); changes.Add(new(index, delta));
    }
    /// <summary>完整参数必须是合法 JSON 对象，不猜测或修补截断内容。</summary>
    private void CompleteTool(int index, List<StreamChange> changes)
    {
        if (!_emittedTools.Add(index)) return;
        var block = _message.Blocks[index];
        if (Text(block["arguments"]) == "" && protocol == LlmProtocol.ClaudeMessages) block["arguments"] = "{}";
        _ = Object(JsonNode.Parse(Text(block["arguments"]))); _ = Text(block["id"]); _ = Text(block["name"]);
        _toolIds.Register(Text(block["id"]), Text(block["name"]));
        changes.Add(new(index, Tool: true));
    }
    /// <summary>工具完成并输出后不能继续修改参数。</summary>
    private void AppendArguments(int index, JsonNode? value)
    {
        if (_emittedTools.Contains(index)) throw new InvalidDataException("已完成的工具参数不能继续追加。");
        if (value is not null) CheckArgumentSize(index, Text(value), append: true);
        Append(_message.Blocks[index], "arguments", value);
    }
    /// <summary>完整原生参数和后续增量使用同一个上限，超限前不写入聚合文档。</summary>
    private void SetArguments(int index, string value)
    {
        CheckArgumentSize(index, value, append: false); _message.Blocks[index]["arguments"] = value;
    }
    /// <summary>按实际 UTF-8 字节计数；只在请求上下文明确启用工具映射时限制所有调用。</summary>
    private void CheckArgumentSize(int index, string value, bool append)
    {
        if (maximumArgumentBytes is not { } maximum) return;
        var bytes = StrictUtf8.GetByteCount(value) + (append ? _argumentSizes.GetValueOrDefault(index) : 0L);
        if (bytes > maximum) ToolRequestCodec.Reject("$event.arguments", "custom_tool_input_limit");
        _argumentSizes[index] = bytes;
    }
    /// <summary>拼接字符串增量。</summary>
    private static void Append(JsonObject block, string name, JsonNode? delta)
    {
        if (delta is not null) block[name] = (block[name]?.GetValue<string>() ?? "") + Text(delta);
    }
}
