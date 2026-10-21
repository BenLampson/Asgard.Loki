using System.Text.Json;

namespace Asgard.Loki.LLM.Cohere.Internal;

/// <summary>只验证 Cohere 原生生命周期，不拼接工具参数、重排引用或补造完成状态。</summary>
internal sealed class CohereStreamState
{
    private string? _generation;
    private bool _ended;
    /// <summary>已遇到原生 stream-end；失败结束原因也保留在原始事件中。</summary>
    internal bool Ended => _ended;
    /// <summary>完整 JSON 只要求原生必填 text，不强加可选 ID、结束原因或工具输出。</summary>
    internal static void Completion(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Cohere 完整响应缺少原生 text 字符串。");
        foreach (var name in new[] { "generation_id", "response_id", "finish_reason" }) OptionalText(data, name);
        if (data.TryGetProperty("tool_calls", out var calls) && calls.ValueKind != JsonValueKind.Null) ToolCalls(calls);
    }
    /// <summary>event_type 唯一决定事件角色，根业务 type 或嵌套标志没有生命周期意义。</summary>
    internal void Push(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Cohere 记录必须是原生对象。");
        var type = RequiredText(data, "event_type");
        if (data.TryGetProperty("is_finished", out var legacy)
            && (legacy.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || legacy.GetBoolean() != (type == "stream-end")))
            throw new InvalidDataException("Cohere 可选 is_finished 与原生事件类型不一致。");
        var generation = OptionalText(data, "generation_id");
        if (type == "stream-start")
        {
            if (_generation is not null || _ended) throw new InvalidDataException("Cohere 重复或迟到的 stream-start。");
            _generation = RequiredText(data, "generation_id"); return;
        }
        if (generation is not null && generation != _generation) throw new InvalidDataException("Cohere 原生生成 ID 改变。");
        if (type == "stream-end")
        {
            if (_generation is null || _ended) throw new InvalidDataException("Cohere stream-end 没有对应唯一 stream-start。");
            var reason = RequiredText(data, "finish_reason");
            if (!data.TryGetProperty("response", out var response)) throw new InvalidDataException("Cohere 终态缺少完整原生 response。");
            Completion(response);
            if (OptionalText(response, "generation_id") is { } id && id != _generation) throw new InvalidDataException("Cohere 终态生成 ID 与开始事件不一致。");
            if (OptionalText(response, "finish_reason") is { } nested && nested != reason) throw new InvalidDataException("Cohere 内外结束原因不一致。");
            _ended = true; return;
        }
        var generationEvent = type is "text-generation" or "search-queries-generation" or "search-results" or "citation-generation" or "tool-calls-generation" or "tool-calls-chunk";
        if (generationEvent && (_generation is null || _ended)) throw new InvalidDataException("Cohere 生成事件位于原生开始与结束边界之外。");
        if (type == "text-generation" && (!data.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String))
            throw new InvalidDataException("Cohere 文本增量缺少原生 text 字符串。");
        if (type is "citation-generation" or "search-queries-generation" or "tool-calls-generation")
        {
            var name = type == "citation-generation" ? "citations" : type == "search-queries-generation" ? "search_queries" : "tool_calls";
            if (!data.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Cohere 事件缺少原生 " + name + " 数组。");
            if (type == "tool-calls-generation") { ToolCalls(array); OptionalText(data, "text"); }
        }
        if (type == "tool-calls-chunk")
        {
            if (!data.TryGetProperty("tool_call_delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Cohere 工具增量缺少原生 tool_call_delta 对象。");
            foreach (var name in new[] { "name", "parameters", "text" }) OptionalText(delta, name);
            OptionalText(data, "text");
            if (delta.TryGetProperty("index", out var index) && index.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null))
                throw new InvalidDataException("Cohere 工具分片索引必须为原生数字或 null。");
        }
    }
    /// <summary>工具名及参数对象原样保留，允许重名、空数组和任意原生参数，不按名字重关联。</summary>
    private static void ToolCalls(JsonElement calls)
    {
        if (calls.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Cohere tool_calls 必须是原生数组。");
        foreach (var call in calls.EnumerateArray())
            if (call.ValueKind != JsonValueKind.Object || !call.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                || !call.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Cohere 工具调用缺少原生 name 字符串或 parameters 对象。");
    }
    /// <summary>正常 EOF 仍需实际原生结束事件，不能由连接关闭或旧标志补造。</summary>
    internal void Complete()
    { if (!_ended) throw new InvalidDataException("Cohere 流缺少 stream-end。"); }
    /// <summary>原生必填身份和事件标签不能用默认值替代。</summary>
    private static string RequiredText(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException("Cohere 原生事件缺少有效的 " + name + "。");
        return value.GetString()!;
    }
    /// <summary>缺省与 null 原样保留；存在的非空类型必须为原生字符串。</summary>
    private static string? OptionalText(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Cohere 原生 " + name + " 类型无效。");
        return value.GetString();
    }
}
