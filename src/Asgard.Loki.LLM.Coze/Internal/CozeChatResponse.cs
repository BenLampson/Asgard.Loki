using System.Text.Json;

namespace Asgard.Loki.LLM.Coze.Internal;

/// <summary>原生 code/Chat/Message 分层校验，不把 API 受理、状态或多条回答归一化成完成文本。</summary>
internal static class CozeChatResponse
{
    /// <summary>仅解释原生根 code，保留非零原文给上层错误，不猜测底层模型或计费。</summary>
    internal static long Code(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt64(out var value))
            throw new InvalidDataException("Coze 原生结果缺少整数 code。");
        return value;
    }
    /// <summary>回执/状态的 Chat ID 与查询保持一致，Bot/Conversation 不能被换成 Message ID。</summary>
    internal static CozeChatReference Chat(JsonElement root, CozeChatTarget target, CozeChatReference? expected, string? conversation)
    {
        if (!root.TryGetProperty("data", out var chat) || chat.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Coze 回执缺少原生 data 对象。");
        var id = Text(chat, "id"); var nativeConversation = Text(chat, "conversation_id"); Text(chat, "status");
        if (expected is not null && expected.ChatId != id || (expected?.ConversationId ?? conversation) is { } required && required != nativeConversation)
            throw new InvalidDataException("Coze Chat/Conversation 身份与原始请求不一致。");
        Echo(chat, "bot_id", target.Options.BotId);
        return new() { TargetIdentity = target.Identity, ChatId = id, ConversationId = nativeConversation };
    }
    /// <summary>整组原生消息顺序不变；Message.id 独立，只有 chat_id 参与 Chat 关联。</summary>
    internal static void Messages(JsonElement root, CozeChatTarget target, CozeChatReference reference)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Coze 消息结果缺少原生 data 数组。");
        foreach (var message in data.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Coze 原生消息必须为对象。");
            Text(message, "id"); Echo(message, "chat_id", reference.ChatId); Echo(message, "conversation_id", reference.ConversationId); Echo(message, "bot_id", target.Options.BotId);
        }
    }
    /// <summary>已知原生关联存在时必须是匹配字符串；缺省不补造字段。</summary>
    internal static void Echo(JsonElement data, string name, string expected)
    { if (data.TryGetProperty(name, out var value) && (value.ValueKind != JsonValueKind.String || value.GetString() != expected)) throw new InvalidDataException("Coze 原生 " + name + " 关联不一致。"); }
    /// <summary>原生必填身份/状态必须为有效字符串，不改写合法原文。</summary>
    internal static string Text(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new InvalidDataException("Coze 缺少原生 " + name + " 字符串。");
        return value.GetString()!;
    }
}
