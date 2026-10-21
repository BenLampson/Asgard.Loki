using System.Text.Json;

namespace Asgard.Loki.LLM.MiniMax.Internal;

/// <summary>原生 v2 choice 结束及可选完整消息快照；不聚合工具参数或生成本地 DONE。</summary>
internal sealed class MiniMaxStreamState(int maximumChoices)
{
    private readonly Dictionary<long, string?> _choices = [];
    private string? _id;
    private bool _snapshot;
    private bool _done;
    internal bool Finishing { get; private set; }
    /// <summary>完整 JSON 必须包含有原生结束原因的消息结果，不把任意对象视为生成成功。</summary>
    internal static void Completion(JsonElement data, int maximumChoices)
    {
        var state = new MiniMaxStreamState(maximumChoices); state.Push(data, complete: true); state.Complete();
    }
    /// <summary>精确关联稀疏索引，允许结束分块后返回一次原生完整消息快照。</summary>
    internal void Push(JsonElement data, bool complete = false)
    {
        if (_done) throw new InvalidDataException("MiniMax SSE 在实际 DONE 后追加事件。");
        if (!data.TryGetProperty("choices", out var choices)) { if (complete) throw new InvalidDataException("MiniMax 完整结果缺少 choices。"); return; }
        if (choices.ValueKind != JsonValueKind.Array) throw new InvalidDataException("MiniMax choices 必须是数组。");
        if (choices.GetArrayLength() == 0) { if (complete) throw new InvalidDataException("MiniMax 完整结果缺少消息 choice。"); return; }
        if (_snapshot) throw new InvalidDataException("MiniMax 完整消息快照后不能重新生成或重复快照。");
        if (data.TryGetProperty("id", out var id))
        {
            if (id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString()) || _id is not null && _id != id.GetString()) throw new InvalidDataException("MiniMax 响应身份无效或发生变化。");
            _id = id.GetString();
        }
        var snapshot = complete || data.TryGetProperty("object", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "chat.completion";
        var unique = new HashSet<long>();
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number || !index.TryGetInt64(out var value) || value < 0 || !unique.Add(value)) throw new InvalidDataException("MiniMax choice 索引无效或重复。");
            if (!_choices.TryGetValue(value, out var ended)) { if (_choices.Count >= maximumChoices) throw new InvalidDataException("MiniMax choice 关联数超过上限。"); _choices.Add(value, null); }
            string? finish = null;
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
            { if (reason.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(reason.GetString())) throw new InvalidDataException("MiniMax 原生结束原因无效。"); finish = reason.GetString(); }
            if (snapshot)
            {
                if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object || finish is null || ended is not null && ended != finish) throw new InvalidDataException("MiniMax 完整快照的消息/结束原因无效或不一致。");
            }
            else
            {
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) throw new InvalidDataException("MiniMax delta 必须是对象或 null。");
                if (ended is not null && (finish is not null || choice.TryGetProperty("message", out _) || Generates(choice))) throw new InvalidDataException("MiniMax choice 结束后重复结束或重新生成内容。");
            }
            if (finish is not null) { _choices[value] = finish; Finishing = true; }
        }
        if (snapshot) { _snapshot = true; Complete(); }
    }
    /// <summary>若上游额外发送实际 DONE，只作为已结束生成后的控制记录；不以它代替原生结束。</summary>
    internal void Done() { if (_done) throw new InvalidDataException("MiniMax 重复 DONE。"); Complete(); _done = true; }
    /// <summary>正常 EOF 仍需全部已观察 choice 有明确原生结束原因。</summary>
    internal void Complete() { if (_choices.Count == 0 || _choices.Values.Any(value => value is null)) throw new InvalidDataException("MiniMax SSE 缺少原生 choice 结束。"); }
    /// <summary>只识别原生生成载荷，未知业务元数据不按名称前缀猜测。</summary>
    private static bool Generates(JsonElement choice)
    {
        if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) return false;
        foreach (var name in new[] { "content", "reasoning_content", "audio_content", "tool_calls", "function_call" })
            if (delta.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null && !(value.ValueKind == JsonValueKind.String && value.GetString()!.Length == 0) && !(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0)) return true;
        return false;
    }
}
