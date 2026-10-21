using System.Text.Json;

namespace Asgard.Loki.LLM.OpenAI.Internal;

/// <summary>原生连接的严格关联字段校验；未知数据字段不被删改。</summary>
internal sealed partial class ResponsesWebSocketState
{
    private static readonly HashSet<string> OutputEvents = new(StringComparer.Ordinal)
    {
        "response.output_item.added", "response.output_item.done", "response.content_part.added", "response.content_part.done",
        "response.output_text.delta", "response.output_text.done", "response.output_text.annotation.added", "response.refusal.delta", "response.refusal.done",
        "response.function_call_arguments.delta", "response.function_call_arguments.done", "response.custom_tool_call_input.delta", "response.custom_tool_call_input.done",
        "response.reasoning_summary_part.added", "response.reasoning_summary_part.done", "response.reasoning_summary_text.delta", "response.reasoning_summary_text.done",
        "response.reasoning_text.delta", "response.reasoning_text.done", "response.audio.delta", "response.audio.done", "response.audio.transcript.delta", "response.audio.transcript.done",
        "response.web_search_call.in_progress", "response.web_search_call.searching", "response.web_search_call.completed",
        "response.file_search_call.in_progress", "response.file_search_call.searching", "response.file_search_call.completed",
        "response.mcp_call_arguments.delta", "response.mcp_call_arguments.done", "response.mcp_call.completed", "response.mcp_call.failed", "response.mcp_call.in_progress",
        "response.mcp_list_tools.completed", "response.mcp_list_tools.failed", "response.mcp_list_tools.in_progress",
        "response.code_interpreter_call.in_progress", "response.code_interpreter_call.interpreting", "response.code_interpreter_call.completed",
        "response.code_interpreter_call_code.delta", "response.code_interpreter_call_code.done",
        "response.image_generation_call.in_progress", "response.image_generation_call.generating", "response.image_generation_call.completed", "response.image_generation_call.partial_image",
        "response.shell_call_command.added", "response.shell_call_command.delta", "response.shell_call_command.done", "response.shell_call_output_content.delta", "response.shell_call_output_content.done",
        "response.compaction.compacting"
    };
    /// <summary>仅字段缺失或显式 null 表示没有可选引用；类型错误不能被当作缺失。</summary>
    private static string? Optional(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new InvalidDataException($"{name} 必须为非空文本。");
        return value.GetString();
    }
    /// <summary>必须存在的协议关联字段。</summary>
    private static string Required(JsonElement data, string name) => Optional(data, name) ?? throw new InvalidDataException($"缺少 {name}。");
    /// <summary>命名 lane 使用协议约定的 ASCII 标识；缺失表示隐式默认 lane。</summary>
    private static string Lane(JsonElement data)
    {
        if (!data.TryGetProperty("stream_id", out _)) return "";
        var lane = Required(data, "stream_id");
        if (lane.Length > 256 || lane.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.')))
            throw new InvalidDataException("stream_id 必须为 1–256 位 ASCII 字母、数字、下划线、连字符或句点。");
        return lane;
    }
    /// <summary>仅验证真实提供的序号，不生成或重排上游序号。</summary>
    private static void Sequence(JsonElement data, ResponsesWebSocketResponseState response)
    {
        if (!data.TryGetProperty("sequence_number", out var number)) return;
        if (number.ValueKind != JsonValueKind.Number || !number.TryGetInt64(out var value) || value < 0 || response.Sequence is not null && value <= response.Sequence)
            throw new InvalidDataException("原生响应事件序号无效或发生倒退。");
        response.Sequence = value;
    }
    /// <summary>只验证明确已知的输出事件，不能以名称前缀误判未来原生事件。</summary>
    private static bool IsOutputEvent(string type) => OutputEvents.Contains(type);
}
