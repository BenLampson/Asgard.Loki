using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>独立维护原生流的协议状态和完整性校验。</summary>
internal sealed partial class SourceStream
{
    private bool _claudeStarted;
    private bool _claudeFinished;
    private readonly Dictionary<int, string> _claudeOpen = [];
    private readonly HashSet<int> _claudeSeen = [];
    /// <summary>Claude 的工具参数在 content_block_stop 后才具备完整 JSON。</summary>
    private void Claude(JsonObject data, List<StreamChange> changes)
    {
        var type = Text(data["type"]);
        Check(data, Document, "$event", type switch
        {
            "message_start" => ["type", "message"], "content_block_start" => ["type", "index", "content_block"],
            "content_block_delta" => ["type", "index", "delta"], "content_block_stop" => ["type", "index"],
            "message_delta" => ["type", "delta", "usage"], _ => ["type"]
        });
        if (type is "content_block_start" or "content_block_delta" or "content_block_stop" or "message_delta" or "message_stop"
            && !_claudeStarted) throw new InvalidDataException("Claude 流缺少 message_start。");
        if (_claudeFinished && type != "message_stop" && type != "ping") throw new InvalidDataException("Claude 消息已结束，不能继续修改内容或用量。");
        switch (type)
        {
            case "message_start":
                if (_claudeStarted) throw new InvalidDataException("Claude message_start 重复。");
                _claudeStarted = true;
                var message = Object(data["message"]); Identity(message, "id", "model");
                Check(message, Document, "$event.message", "id", "type", "role", "model", "content", "stop_reason", "stop_sequence", "usage");
                if (Text(message["type"]) != "message" || Text(message["role"]) != "assistant" || Array(message["content"]).Count != 0)
                    throw new InvalidDataException("Claude message_start 结构无效。");
                UsageMapping.Read(message["usage"], protocol, Document);
                break;
            case "content_block_start":
                var content = Object(data["content_block"]); var key = "claude-" + data["index"]!.GetValue<int>();
                var blockType = Text(content["type"]);
                var sourceIndex = data["index"]!.GetValue<int>();
                if (sourceIndex < 0 || !_claudeSeen.Add(sourceIndex) || _claudeOpen.Count != 0 || sourceIndex != _claudeSeen.Count - 1)
                    throw new InvalidDataException("Claude 内容块索引重复、无序或上一个内容块未关闭。");
                _claudeOpen.Add(sourceIndex, blockType);
                if (blockType == "text") { Check(content, Document, "$event.content_block", "type", "text"); TextDelta(key, Text(content["text"]), changes); }
                else if (blockType == "tool_use")
                {
                    Check(content, Document, "$event.content_block", "type", "id", "name", "input");
                    var index = Block(key, "call"); var block = _message.Blocks[index];
                    block["id"] = Text(content["id"]); block["name"] = Text(content["name"]);
                    if (Object(content["input"]).Count != 0) block["arguments"] = content["input"]!.ToJsonString();
                }
                else if (blockType is "thinking" or "redacted_thinking")
                    throw new LlmConversionException([new("opaque_state", "$event.content_block", "推理签名必须使用 Claude 原生协议。")]);
                else Loss(Document, "$event.content_block." + blockType, "unsupported_content");
                break;
            case "content_block_delta":
                var delta = Object(data["delta"]); var deltaType = Text(delta["type"]); var blockKey = "claude-" + data["index"]!.GetValue<int>();
                if (!_claudeOpen.TryGetValue(data["index"]!.GetValue<int>(), out var openType)) throw new InvalidDataException("增量引用了未开始或已关闭的 Claude 内容块。");
                if (deltaType == "text_delta" && openType != "text" || deltaType == "input_json_delta" && openType != "tool_use")
                    throw new InvalidDataException("Claude 增量类型与内容块不匹配。");
                if (deltaType == "text_delta") { Check(delta, Document, "$event.delta", "type", "text"); TextDelta(blockKey, Text(delta["text"]), changes); }
                else if (deltaType == "input_json_delta")
                {
                    Check(delta, Document, "$event.delta", "type", "partial_json");
                    AppendArguments(Existing(blockKey), delta["partial_json"]);
                }
                else if (deltaType is "thinking_delta" or "signature_delta")
                    throw new LlmConversionException([new("opaque_state", "$event.delta", "推理签名必须使用 Claude 原生协议。")]);
                else Loss(Document, "$event.delta." + deltaType, "unsupported_content");
                break;
            case "content_block_stop":
                if (!_claudeOpen.Remove(data["index"]!.GetValue<int>())) throw new InvalidDataException("Claude 内容块未打开或已经关闭。");
                if (_indices.TryGetValue("claude-" + data["index"]!.GetValue<int>(), out var stopped)
                    && Text(_message.Blocks[stopped]["type"]) == "call") CompleteTool(stopped, changes);
                break;
            case "message_delta":
                if (_claudeOpen.Count != 0) throw new InvalidDataException("Claude 消息结束前仍有未关闭的内容块。");
                var terminal = Object(data["delta"]); Check(terminal, Document, "$event.delta", "stop_reason", "stop_sequence");
                if (terminal["stop_sequence"] is not null) Loss(Document, "$event.delta.stop_sequence");
                if (terminal["stop_reason"] is not null) { Document.Finish = ResponseMapping.Finish(Text(terminal["stop_reason"]), protocol); _claudeFinished = true; }
                UsageMapping.Read(data["usage"], protocol, Document);
                break;
            case "message_stop":
                if (!_claudeFinished || _claudeOpen.Count != 0) throw new InvalidDataException("Claude 结束原因或内容块终态缺失。");
                _terminal = true; break;
            case "ping": break;
            default: Loss(Document, "$event." + type, "unknown_event"); break;
        }
    }

}
