using System.Text.Json;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>两个原生流共有文本/普通工具状态，保留稀疏索引而不合并或重排块。</summary>
internal sealed class BedrockStreamState(LlmConversionOptions options, BedrockStreamFields fields, LlmConversionContext? context)
{
    private readonly HashSet<int> _seen = [];
    private readonly Dictionary<int, string> _open = [];
    private readonly Dictionary<int, BedrockStreamToolArguments> _arguments = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private bool _started;
    private bool _stopped;
    private bool _metadata;
    /// <summary>消息终止及后续元数据只暂存，不在源正常结束前交付成功。</summary>
    internal bool Finishing => _stopped;
    /// <summary>已识别的原生逻辑事件使用严格源关联，而非损耗策略来校验结构。</summary>
    internal JsonObject Map(string type, JsonObject data, string path)
    {
        if (_stopped && type != "metadata") throw Invalid();
        switch (type)
        {
            case "messageStart":
                if (_started || ReadText(data["role"]) != "assistant") throw Invalid(); _started = true;
                return fields.Pick(data, path, "role");
            case "contentBlockStart":
                RequireContent(); var startIndex = Index(data); var start = Object(data["start"]);
                NativeOnly(start, path + ".start");
                if (!start.ContainsKey("toolUse")) BedrockConversion.Reject(path + ".start");
                var tool = Object(start["toolUse"]); var id = ReadText(tool["toolUseId"]); _ = ReadText(tool["name"]);
                if (tool.ContainsKey("input") || tool.ContainsKey("type")) BedrockConversion.Reject(path + ".start.toolUse");
                if (!_ids.Add(id)) throw Invalid();
                if (context?.HistoricalIds.Contains(id) == true) BedrockConversion.Reject(path + ".start.toolUse.toolUseId", "tool_call_id_reused");
                Open(startIndex, "tool", path); _arguments.Add(startIndex, new(options.MaxBedrockStreamToolArgumentBytes ?? 1024 * 1024));
                var copiedStart = fields.Pick(start, path + ".start", "toolUse");
                copiedStart["toolUse"] = fields.Pick(tool, path + ".start.toolUse", "toolUseId", "name");
                var copied = fields.Pick(data, path, "contentBlockIndex", "start"); copied["start"] = copiedStart; return copied;
            case "contentBlockDelta":
                RequireContent(); return Delta(data, path);
            case "contentBlockStop":
                RequireContent(); var stopIndex = Index(data);
                if (!_open.Remove(stopIndex)) throw Invalid();
                if (_arguments.Remove(stopIndex, out var arguments)) arguments.Complete();
                return fields.Pick(data, path, "contentBlockIndex");
            case "messageStop":
                RequireContent(); var reason = ReadText(data["stopReason"]);
                if (_open.Count != 0 || reason == "tool_use" && _ids.Count == 0 || _ids.Count != 0 && reason != "tool_use") throw Invalid();
                _stopped = true; return fields.Pick(data, path, "stopReason");
            case "metadata":
                if (!_stopped || _metadata) throw Invalid(); _metadata = true;
                var usage = Object(data["usage"]); var metrics = Object(data["metrics"]);
                foreach (var key in new[] { "inputTokens", "outputTokens", "totalTokens" }) Counter(usage[key]);
                Counter(metrics["latencyMs"]);
                var metadata = fields.Pick(data, path, "usage", "metrics");
                metadata["usage"] = fields.Pick(usage, path + ".usage", "inputTokens", "outputTokens", "totalTokens");
                metadata["metrics"] = fields.Pick(metrics, path + ".metrics", "latencyMs"); return metadata;
            default: throw Invalid();
        }
    }
    /// <summary>正常源 EOF 本身不能替代消息、块或用量终态。</summary>
    internal void Complete()
    {
        if (!_started || !_stopped || !_metadata || _open.Count != 0 || _arguments.Count != 0) throw Invalid();
    }
    /// <summary>文本允许隐式开始；工具增量必须指向已明确开始且仍打开的原始块。</summary>
    private JsonObject Delta(JsonObject data, string path)
    {
        var index = Index(data); var delta = Object(data["delta"]); NativeOnly(delta, path + ".delta");
        if (delta.ContainsKey("text") == delta.ContainsKey("toolUse")) throw Invalid();
        var kind = delta.ContainsKey("text") ? "text" : "tool";
        if (!_open.TryGetValue(index, out var actual))
        { if (_seen.Contains(index) || kind == "tool") throw Invalid(); Open(index, kind, path); }
        else if (actual != kind) throw Invalid();
        var copy = fields.Pick(data, path, "contentBlockIndex", "delta");
        if (kind == "text")
        {
            _ = ReadString(delta["text"]); copy["delta"] = fields.Pick(delta, path + ".delta", "text");
        }
        else
        {
            var tool = Object(delta["toolUse"]);
            if (tool.ContainsKey("toolUseId") || tool.ContainsKey("name")) throw Invalid();
            var input = ReadString(tool["input"]); _arguments[index].Append(input, path + ".delta.toolUse.input");
            var mapped = fields.Pick(delta, path + ".delta", "toolUse"); mapped["toolUse"] = fields.Pick(tool, path + ".delta.toolUse", "input"); copy["delta"] = mapped;
        }
        return copy;
    }
    /// <summary>整个流按字典/集合计数；极大但合法的原生索引不会导致数组分配。</summary>
    private void Open(int index, string kind, string path)
    {
        if (_seen.Contains(index)) throw Invalid();
        if (_seen.Count >= (options.MaxBedrockStreamBlocks ?? 128)) BedrockConversion.Reject(path + ".contentBlockIndex", "bedrock_stream_block_limit");
        _seen.Add(index); _open.Add(index, kind);
    }
    /// <summary>不透明推理、服务工具结果、媒体和引用必须继续走原生协议。</summary>
    private static void NativeOnly(JsonObject data, string path)
    {
        if (data.Any(field => field.Key is "reasoningContent" or "toolResult" or "image" or "video" or "document" or "citation" or "audio" or "guardContent" or "cachePoint"))
            BedrockConversion.Reject(path);
    }
    /// <summary>消息结束后或开始前不能出现内容。</summary>
    private void RequireContent() { if (!_started || _stopped) throw Invalid(); }
    /// <summary>原始索引必须为非负 int32 数值，不按字符串或浮点舍入关联。</summary>
    private static int Index(JsonObject data)
    {
        var value = JsonSerializer.SerializeToElement(data["contentBlockIndex"]);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var index) || index < 0) throw Invalid(); return index;
    }
    /// <summary>必需身份和结束标签必须是非空原生字符串。</summary>
    private static string ReadText(JsonNode? node)
    {
        var value = ReadString(node); return string.IsNullOrWhiteSpace(value) ? throw Invalid() : value;
    }
    /// <summary>文本增量允许空字符串，数值/数组/null 不按字符串推断。</summary>
    private static string ReadString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && text is not null ? text : throw Invalid();
    /// <summary>已知结构节点必须是对象，畸形原生源统一明确失败。</summary>
    private static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw Invalid();
    /// <summary>计数必须明确存在且为非负原生整数，不合成未知数据。</summary>
    private static void Counter(JsonNode? node)
    {
        var value = JsonSerializer.SerializeToElement(node);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var count) || count < 0) throw Invalid();
    }
    /// <summary>固定错误不回显源文本或工具参数。</summary>
    private static InvalidDataException Invalid() => new("Bedrock 转换流的消息、块、工具关联或原生终态无效或不完整。");
}
