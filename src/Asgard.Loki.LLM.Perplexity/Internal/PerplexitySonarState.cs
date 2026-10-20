using System.Text.Json;

namespace Asgard.Loki.LLM.Perplexity.Internal;

/// <summary>原生 Sonar 的 choice、全局结束及元数据边界，不聚合消息或改写关联标识。</summary>
internal sealed class PerplexitySonarState(int maximumChoices)
{
    private readonly Dictionary<long, string?> _choices = [];
    private string? _id;
    private bool _global;
    private bool _done;
    private bool _native;
    internal bool Finishing { get; private set; }
    /// <summary>完整原生 JSON 保留状态和选择，不把同步结果改写为任务或补发结束原因。</summary>
    internal static void Completion(JsonElement data, int maximumChoices)
    {
        if (!data.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString())
            || !data.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(model.GetString())
            || !data.TryGetProperty("created", out var created) || created.ValueKind != JsonValueKind.Number || !created.TryGetInt64(out _)
            || !data.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Perplexity Sonar 完整结果缺少原生结构。");
        foreach (var choice in choices.EnumerateArray())
            if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Perplexity Sonar 完整 choice 缺少消息。");
        new PerplexitySonarState(maximumChoices).Push(data, null);
    }
    /// <summary>仅原生帧解释响应身份；未知业务事件保持原文，包括同名 id/error 字段。</summary>
    internal void Push(JsonElement data, string? eventName)
    {
        if (_done) throw new InvalidDataException("Perplexity Sonar DONE 后出现数据。");
        var type = data.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() : null;
        var hasChoices = data.TryGetProperty("choices", out var choices);
        if (type is not (null or "message" or "info" or "end_of_stream")) return;
        if (!hasChoices && type is null) return;
        if (kind.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String)) throw new InvalidDataException("Perplexity Sonar 原生 type 无效。");
        _native = true;
        if (hasChoices && choices.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Perplexity Sonar choices 必须是数组。");
        if (data.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null)
        {
            if (id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString()) || _id is not null && _id != id.GetString()) throw new InvalidDataException("Perplexity Sonar 响应身份无效或变化。"); _id = id.GetString();
        }
        if (data.TryGetProperty("status", out var status) && status.ValueKind != JsonValueKind.Null
            && (status.ValueKind != JsonValueKind.String || status.GetString() is not ("PENDING" or "COMPLETED"))) throw new InvalidDataException("Perplexity Sonar 原生状态无效。");
        var ending = type == "end_of_stream";
        if (_global && (ending || type == "message" || hasChoices && choices.GetArrayLength() != 0)) throw new InvalidDataException("Perplexity Sonar 全局结束后重新生成或重复结束。");
        if (ending && status.ValueKind == JsonValueKind.String && status.GetString() == "PENDING") throw new InvalidDataException("Perplexity Sonar 全局结束仍为 PENDING。");
        if (hasChoices)
        {
            var unique = new HashSet<long>();
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number || !index.TryGetInt64(out var number) || number < 0 || !unique.Add(number)) throw new InvalidDataException("Perplexity Sonar choice 索引无效或重复。");
                if (!_choices.TryGetValue(number, out var ended)) { if (_choices.Count >= maximumChoices) throw new InvalidDataException("Perplexity Sonar choice 数量超过上限。"); _choices.Add(number, null); }
                string? finish = null;
                if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
                { if (reason.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(reason.GetString())) throw new InvalidDataException("Perplexity Sonar 结束原因无效。"); finish = reason.GetString(); }
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object)) throw new InvalidDataException("Perplexity Sonar delta 无效。");
                var message = choice.TryGetProperty("message", out var snapshot) && snapshot.ValueKind == JsonValueKind.Object;
                if (ended is not null && (finish is not null || Generates(delta) || message)
                    && !(ending && message && !Generates(delta) && (finish is null || finish == ended))) throw new InvalidDataException("Perplexity Sonar 已结束 choice 重复结束或重新生成。");
                if (finish is not null) { _choices[number] = finish; Finishing = true; }
            }
        }
        if (ending) { _global = true; Finishing = true; }
    }
    /// <summary>实际 DONE 是原生控制终态，不能先于任何原生帧或重复出现。</summary>
    internal void Done()
    { if (_done || !_native) throw new InvalidDataException("Perplexity Sonar DONE 提前或重复。"); _done = true; Finishing = true; }
    /// <summary>EOF 不能自行结束未完成的原生调用，也不补造 DONE 或消息。</summary>
    internal void Complete()
    { if (!_global && !_done && (_choices.Count == 0 || _choices.Values.Any(value => value is null))) throw new InvalidDataException("Perplexity Sonar 流缺少原生终态。"); }
    private static bool Generates(JsonElement delta)
    {
        if (delta.ValueKind != JsonValueKind.Object) return false;
        foreach (var name in new[] { "role", "content", "reasoning_content", "reasoning_steps", "tool_calls", "function_call" })
            if (delta.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null && !(value.ValueKind == JsonValueKind.String && value.GetString()!.Length == 0) && !(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0)) return true;
        return false;
    }
}
