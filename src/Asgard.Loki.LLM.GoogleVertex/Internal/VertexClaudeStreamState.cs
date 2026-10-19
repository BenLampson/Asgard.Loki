using System.Text.Json;

namespace Asgard.Loki.LLM.GoogleVertex.Internal;

/// <summary>原生 Claude 消息/内容块关联；不组装 JSON 参数、文本、签名或用量。</summary>
internal sealed class VertexClaudeStreamState(int maximumBlocks)
{
    private readonly HashSet<int> _seen = [];
    private readonly Dictionary<int, string> _open = [];
    private readonly HashSet<string> _toolIds = new(StringComparer.Ordinal);
    private bool _started;
    private bool _terminal;
    private string? _stopReason;
    /// <summary>可能结束或原生错误开始暂存，正常 EOF 和释放后才能交付。</summary>
    internal bool Finishing => _stopReason is not null || _terminal;
    /// <summary>只有原生 message_stop 或 error 能结束创建，EOF 本身不生成终态。</summary>
    internal void Observe(JsonElement data, string? eventName)
    {
        var type = Type(data, eventName);
        if (Known(eventName) && type != eventName) throw new InvalidDataException("Vertex Claude 已知 SSE 名称与 JSON 类型不一致。");
        if (type == "error")
        {
            if (_terminal) throw new InvalidDataException("Vertex Claude 重复原生终态。");
            if (!data.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Vertex Claude error 事件缺少原生错误对象。"); _terminal = true; return;
        }
        if (_terminal && Known(type)) throw new InvalidDataException("Vertex Claude 终态后追加消息或内容事件。");
        switch (type)
        {
            case "message_start":
                if (_started || !data.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString())) throw new InvalidDataException("Vertex Claude message_start 缺失身份或重复。");
                if (message.TryGetProperty("content", out var initial) && (initial.ValueKind != JsonValueKind.Array || initial.GetArrayLength() != 0)) throw new InvalidDataException("Vertex Claude 创建流起始 content 必须为空数组。");
                _started = true; break;
            case "content_block_start":
                RequireStarted(); if (_stopReason is not null) throw new InvalidDataException("Vertex Claude 消息结束原因之后重新开启内容块。"); var index = Index(data);
                if (!_seen.Add(index)) throw new InvalidDataException("Vertex Claude 重复或重新打开内容块索引。");
                if (_seen.Count > maximumBlocks) throw new InvalidDataException("Vertex Claude 内容块关联数超过配置上限。");
                if (!data.TryGetProperty("content_block", out var block) || block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var kind) || kind.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(kind.GetString())) throw new InvalidDataException("Vertex Claude 内容块缺少原生类型。");
                var blockType = kind.GetString()!;
                if (blockType is "tool_use" or "server_tool_use")
                {
                    if (!block.TryGetProperty("id", out var toolId) || toolId.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(toolId.GetString()) || !_toolIds.Add(toolId.GetString()!)) throw new InvalidDataException("Vertex Claude 工具 ID 缺失或重复。");
                    if (!block.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(name.GetString())) throw new InvalidDataException("Vertex Claude 工具名缺失。");
                }
                _open.Add(index, blockType); break;
            case "content_block_delta":
                RequireStarted(); var deltaIndex = Index(data);
                if (!_open.TryGetValue(deltaIndex, out var sourceType)) throw new InvalidDataException("Vertex Claude 增量引用未打开或已关闭内容块。");
                if (!data.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object || !delta.TryGetProperty("type", out var deltaType) || deltaType.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(deltaType.GetString())) throw new InvalidDataException("Vertex Claude 内容增量缺少原生类型。");
                if (sourceType is "text" or "thinking" or "redacted_thinking" or "tool_use" or "server_tool_use")
                {
                    var compatible = deltaType.GetString() switch { "text_delta" => sourceType == "text", "thinking_delta" or "signature_delta" => sourceType == "thinking", "input_json_delta" => sourceType is "tool_use" or "server_tool_use", _ => true };
                    if (!compatible) throw new InvalidDataException("Vertex Claude 已知增量与所关联内容块类型不符。");
                }
                break;
            case "content_block_stop":
                RequireStarted(); if (!_open.Remove(Index(data))) throw new InvalidDataException("Vertex Claude 关闭未打开或已经关闭的内容块。"); break;
            case "message_delta":
                RequireStarted();
                if (!data.TryGetProperty("delta", out var changes) || changes.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Vertex Claude message_delta 缺少对象。");
                if (changes.TryGetProperty("stop_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
                {
                    if (reason.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(reason.GetString()) || _open.Count != 0 || _stopReason is not null && _stopReason != reason.GetString()) throw new InvalidDataException("Vertex Claude 结束原因无效、改变或尚有未闭合内容块。"); _stopReason = reason.GetString();
                }
                break;
            case "message_stop":
                RequireStarted(); if (_stopReason is null || _open.Count != 0) throw new InvalidDataException("Vertex Claude 缺少消息结束原因或仍有未闭合内容块。"); _terminal = true; break;
        }
    }
    /// <summary>未知事件和 ping 可保留，但不能替代缺失的原生消息终态。</summary>
    internal void Complete()
    {
        if (!_terminal) throw new InvalidDataException("Vertex Claude SSE 在 message_stop 或 error 之前截断。");
    }
    /// <summary>保持 JSON 类型与传输名称独立，缺省类型可使用原始 SSE 名称。</summary>
    internal static string Type(JsonElement data, string? eventName)
    {
        if (data.TryGetProperty("type", out var value))
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString())) throw new InvalidDataException("Vertex Claude SSE 类型无效。"); return value.GetString()!;
        }
        return !string.IsNullOrEmpty(eventName) ? eventName : throw new InvalidDataException("Vertex Claude SSE 缺少事件类型。");
    }
    /// <summary>已知生命周期事件不能绕过原生 message_start。</summary>
    private void RequireStarted()
    {
        if (!_started) throw new InvalidDataException("Vertex Claude 消息开始之前出现内容或消息增量。");
    }
    /// <summary>按数值而非数组位置关联原始内容块，不重编号或按索引分配数组。</summary>
    private static int Index(JsonElement data)
    {
        if (!data.TryGetProperty("index", out var value) || value.ValueKind != JsonValueKind.Number) throw new InvalidDataException("Vertex Claude 内容块缺少原生整数索引。"); return VertexInteger.Read(value);
    }
    /// <summary>未知事件和 ping 是前向兼容元数据，不被误当作新的内容生命周期。</summary>
    private static bool Known(string? type) => type is "message_start" or "message_delta" or "message_stop" or "content_block_start" or "content_block_delta" or "content_block_stop" or "error";
}
