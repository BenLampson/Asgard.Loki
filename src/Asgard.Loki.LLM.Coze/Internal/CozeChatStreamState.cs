using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.LLM.Coze.Internal;

/// <summary>Coze 原生两级状态和关联，不合并回答、解析工具内容或伪造 stop/DONE。</summary>
internal sealed class CozeChatStreamState(CozeChatTarget target, string? expectedConversation)
{
    private CozeChatReference? _reference;
    private bool _created;
    private bool _chatTerminal;
    private bool _done;
    private readonly Dictionary<string, (bool Done, string? Role, string? Type, string? ContentType, bool NeedsCompletion)> _messages = new(StringComparer.Ordinal);
    /// <summary>从首个原生 Chat 结束事件起缓冲后续帧，包括失败/待工具结果而非只缓冲成功。</summary>
    internal bool HoldTail => _chatTerminal;
    /// <summary>错误优先于晚到数据检测，确保实际 DONE 后的原生错误仍保留完整数据。</summary>
    internal CozeChatEvent Push(SseFrame frame, HttpStatusCode status, string? contentType)
    {
        var name = frame.EventName;
        if (name == "error") throw new LlmUpstreamException("Coze 原生 SSE 错误事件。", status, Encoding.UTF8.GetBytes(frame.Data), contentType);
        if (_done) throw new InvalidDataException("Coze 实际 done 后出现重复控制帧或晚到数据。");
        if (name == "done")
        {
            if (!_chatTerminal || _reference is null) throw new InvalidDataException("Coze done 缺少关联的原生 Chat 终态。");
            JsonElement? value = null;
            if (frame.Data != "[DONE]")
            {
                value = CozeJson.Parse(Encoding.UTF8.GetBytes(frame.Data));
                if (value.Value.ValueKind != JsonValueKind.String || value.Value.GetString() != "[DONE]") throw new InvalidDataException("Coze done 缺少实际原生 DONE 数据。");
            }
            _done = true; return Event(frame, value, false, true);
        }
        var chat = name is "conversation.chat.created" or "conversation.chat.in_progress" or "conversation.chat.completed" or "conversation.chat.failed" or "conversation.chat.requires_action";
        var message = name is "conversation.message.delta" or "conversation.message.completed" or "conversation.audio.delta";
        if (!chat && !message) return Event(frame, null, false, false);
        if (_chatTerminal) throw new InvalidDataException("Coze Chat 终态之后出现已知生成事件。");
        var data = CozeJson.Parse(Encoding.UTF8.GetBytes(frame.Data));
        if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Coze 已知 Chat/Message 事件必须是原生对象。");
        var terminal = name is "conversation.chat.completed" or "conversation.chat.failed" or "conversation.chat.requires_action";
        if (chat)
        {
            if (name == "conversation.chat.created")
            {
                if (_created || _reference is not null) throw new InvalidDataException("Coze 重复或迟到的 Chat created。");
                _created = true;
            }
            BindChat(data);
            if (!terminal || name != "conversation.chat.failed") CozeChatResponse.Text(data, "status");
            if (data.TryGetProperty("status", out var nativeStatus) && (nativeStatus.ValueKind != JsonValueKind.String || nativeStatus.GetString() != name!["conversation.chat.".Length..]))
                throw new InvalidDataException("Coze Chat 事件与原生状态不一致。");
            if (name == "conversation.chat.completed" && _messages.Values.Any(m => m.NeedsCompletion && !m.Done))
                throw new InvalidDataException("Coze Chat 完成时仍存在未完成的原生 Message。");
            _chatTerminal = terminal;
        }
        else Message(data, name == "conversation.message.completed", name == "conversation.message.delta");
        return Event(frame, data, terminal, false);
    }
    /// <summary>正常 EOF 不能代替原生 Chat 结果和 native done，不凭连接结束补造完成。</summary>
    internal void Complete()
    { if (!_chatTerminal || !_done) throw new InvalidDataException("Coze 流缺少完整原生 Chat 终态及 done。"); }
    /// <summary>Chat.id 是 Chat 身份，Message.id 不能覆盖它；错误终态可复用已建立的上下文而不插入字段。</summary>
    private void BindChat(JsonElement data)
    {
        if (_reference is null)
        {
            var chatId = CozeChatResponse.Text(data, "id"); var conversation = CozeChatResponse.Text(data, "conversation_id");
            if (expectedConversation is not null && expectedConversation != conversation) throw new InvalidDataException("Coze 流的会话与请求不一致。");
            _reference = new() { TargetIdentity = target.Identity, ChatId = chatId, ConversationId = conversation };
        }
        CozeChatResponse.Echo(data, "id", _reference.ChatId); CozeChatResponse.Echo(data, "chat_id", _reference.ChatId);
        CozeChatResponse.Echo(data, "conversation_id", _reference.ConversationId); CozeChatResponse.Echo(data, "bot_id", target.Options.BotId);
    }
    /// <summary>每个原生消息独立完成，不把多条答案、工具/音频消息或原生关联改造成一个下标零消息。</summary>
    private void Message(JsonElement data, bool completed, bool requiresCompletion)
    {
        if (_reference is null) throw new InvalidDataException("Coze Message 事件缺少已知 Chat 身份。");
        var id = CozeChatResponse.Text(data, "id");
        CozeChatResponse.Echo(data, "chat_id", _reference.ChatId); CozeChatResponse.Echo(data, "conversation_id", _reference.ConversationId); CozeChatResponse.Echo(data, "bot_id", target.Options.BotId);
        if (!_messages.TryGetValue(id, out var previous))
        {
            if (_messages.Count >= target.Options.MaximumTrackedMessages) throw new InvalidDataException("Coze 原生消息关联超过显式上限。");
        }
        else if (previous.Done) throw new InvalidDataException("Coze 已完成 Message 出现重复完成或晚到增量。");
        var role = Stable(data, "role", previous.Role); var type = Stable(data, "type", previous.Type); var content = Stable(data, "content_type", previous.ContentType);
        _messages[id] = (completed, role, type, content, previous.NeedsCompletion || requiresCompletion);
    }
    /// <summary>可选分片属性存在时保持原生字符串和稳定关联，缺省不补写原始事件。</summary>
    private static string? Stable(JsonElement data, string name, string? previous)
    {
        if (!data.TryGetProperty(name, out var value)) return previous;
        if (value.ValueKind != JsonValueKind.String || previous is not null && previous != value.GetString()) throw new InvalidDataException("Coze Message 的 " + name + " 关联发生变化。");
        return value.GetString();
    }
    /// <summary>只建立独立帧快照，不改变原始事件或代替上游权限。</summary>
    private CozeChatEvent Event(SseFrame frame, JsonElement? json, bool chatTerminal, bool terminal)
        => new(frame.Data, frame.EventName, frame.EventId, frame.RetryMilliseconds, json, target.Metadata, _reference, chatTerminal, terminal);
}
