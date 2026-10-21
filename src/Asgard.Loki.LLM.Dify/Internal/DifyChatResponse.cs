using System.Text.Json;

namespace Asgard.Loki.LLM.Dify.Internal;

/// <summary>供应商原生响应约束，字段只在各自 ID 域内关联，不解释业务嵌套值。</summary>
internal static class DifyChatResponse
{
    /// <summary>原生根错误不依赖 HTTP 状态；嵌套工具/知识/业务错误不被误判。</summary>
    internal static bool Error(JsonElement data) => data.ValueKind == JsonValueKind.Object &&
        (data.TryGetProperty("event", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "error"
        || !data.TryGetProperty("event", out _) && data.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(code.GetString()));
    /// <summary>阻塞响应保留消息或 Chatflow 暂停，事件 ID 不等同于 message_id/task_id。</summary>
    internal static void Blocking(JsonElement data, DifyChatProfile profile, string? expectedConversation)
    {
        var kind = Text(data, "event");
        if (kind != "message" && !(kind == "workflow_paused" && profile == DifyChatProfile.Chatflow)) throw new InvalidDataException("Dify 阻塞响应不是原生消息或所选 Chatflow 暂停。");
        _ = Text(data, "id"); _ = Text(data, "message_id"); _ = Text(data, "task_id"); var conversation = Text(data, "conversation_id");
        if (!string.IsNullOrEmpty(expectedConversation) && conversation != expectedConversation) throw new InvalidDataException("Dify 响应属于另一原生会话。");
        if (!data.TryGetProperty("answer", out var answer) || answer.ValueKind != JsonValueKind.String) throw new InvalidDataException("Dify 原生阻塞响应缺少字符串 answer。");
        if (kind == "workflow_paused") _ = Text(data, "workflow_run_id");
    }
    /// <summary>仅解释明确需要的根字符串，不修改原文或推断不存在的值。</summary>
    internal static string Text(JsonElement data, string name)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException("Dify 原生响应缺少有效 " + name + "。");
        return value.GetString()!;
    }
}
