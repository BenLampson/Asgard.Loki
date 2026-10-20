using System.Text.Json;

namespace Asgard.Loki.LLM.OpenAI.Internal;

/// <summary>HTTP 原生终态和稳定响应身份；不聚合、重编号或修补工具/推理内容。</summary>
internal sealed class OpenAiHttpStreamState(bool responses)
{
    private bool _terminal;
    private bool _done;
    private string? _id;
    /// <summary>Chat finish 或 Responses 原生终态/错误之后开始有限尾部暂存。</summary>
    internal bool Finishing { get; private set; }
    /// <summary>识别原生错误位置，不遍历工具参数或未知业务对象。</summary>
    internal static bool IsFailure(JsonElement data, string? eventName, bool responses)
    {
        var type = Type(data, eventName, responses);
        var defaultEvent = string.IsNullOrEmpty(type) || type == "message" && !data.TryGetProperty("type", out _);
        return eventName == "error" || type == "error" || responses && type == "response.failed" || defaultEvent && OpenAiHttpJson.IsError(data);
    }
    /// <summary>保留未知尾帧；已知终态重复、源响应身份变化或结束后重开内容均明确失败。</summary>
    internal void Observe(JsonElement data, string? eventName)
    {
        if (_done) throw new InvalidDataException("OpenAI SSE 在 DONE 后追加事件。");
        var type = Type(data, eventName, responses); var failure = IsFailure(data, eventName, responses);
        if (responses)
        {
            var hasResponse = data.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object;
            var status = type switch { "response.completed" => "completed", "response.incomplete" => "incomplete", "response.failed" => "failed", _ => null };
            var known = status is not null || IsContent(type);
            if (known && hasResponse && response.TryGetProperty("id", out var id)) Identity(id);
            if (known && data.TryGetProperty("response_id", out var responseId)) Identity(responseId);
            if (status is not null || type == "error" || failure)
            {
                if (_terminal) throw new InvalidDataException("OpenAI Responses 重复原生终态。");
                if (status is not null && (!hasResponse || !response.TryGetProperty("id", out var nativeId) || nativeId.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(nativeId.GetString()) || !response.TryGetProperty("status", out var actual) || actual.ValueKind != JsonValueKind.String || actual.GetString() != status)) throw new InvalidDataException("OpenAI Responses 终态缺少一致的原生身份/状态快照。");
                _terminal = true; Finishing = true;
            }
            else if (_terminal && IsContent(type)) throw new InvalidDataException("OpenAI Responses 在终态后重新开始生成。");
        }
        else
        {
            if (failure) { _terminal = true; Finishing = true; }
            if (data.TryGetProperty("choices", out var choices))
            {
                if (choices.ValueKind != JsonValueKind.Array) throw new InvalidDataException("OpenAI Chat choices 必须是数组。");
                // 仅原生 choices 帧的 id 属于 Chat 生成，未知业务事件的同名字段保持原文。
                if (data.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null && !(id.ValueKind == JsonValueKind.String && id.GetString()!.Length == 0)) Identity(id);
                foreach (var choice in choices.EnumerateArray())
                    if (choice.ValueKind == JsonValueKind.Object && choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind != JsonValueKind.Null)
                    { if (finish.ValueKind != JsonValueKind.String || finish.GetString()!.Length == 0) throw new InvalidDataException("OpenAI Chat finish_reason 无效。"); Finishing = true; }
            }
        }
    }
    /// <summary>DONE 是 Chat 原生结束标志，但不能替代 Responses 自己的终态；之后仍需正常 EOF。</summary>
    internal void ObserveDone()
    {
        if (_done || responses && !_terminal) throw new InvalidDataException("OpenAI SSE 的 DONE 重复或提前到达。"); _done = true;
    }
    /// <summary>不会由 EOF 合成 Chat DONE 或 Responses 完成。</summary>
    internal void Complete()
    {
        if (responses ? !_terminal : !_done && !_terminal) throw new InvalidDataException("OpenAI SSE 在原生终态之前截断。");
    }
    /// <summary>存在的原生身份必须非空且一致，不覆盖为调用者模型别名。</summary>
    private void Identity(JsonElement id)
    {
        if (id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString()) || _id is not null && _id != id.GetString()) throw new InvalidDataException("OpenAI SSE 响应身份无效或变化。"); _id = id.GetString();
    }
    /// <summary>类型来自原生 type 或独立 SSE event，未知字符串不猜测为成功。</summary>
    private static string? Type(JsonElement data, string? eventName, bool responses)
    {
        if (!data.TryGetProperty("type", out var type)) return eventName;
        if (type.ValueKind != JsonValueKind.String) { if (responses) throw new InvalidDataException("Responses SSE type 必须是字符串。"); return eventName; }
        return type.GetString();
    }
    /// <summary>仅已知生成/生命周期事件不能在 Responses 结束后重开，未知元数据原样保留。</summary>
    private static bool IsContent(string? type) => type is "response.created" or "response.in_progress" or "response.queued"
        or "response.output_item.added" or "response.output_item.done" or "response.content_part.added" or "response.content_part.done"
        or "response.output_text.delta" or "response.output_text.done" or "response.output_text.annotation.added"
        or "response.function_call_arguments.delta" or "response.function_call_arguments.done"
        or "response.reasoning_text.delta" or "response.reasoning_text.done"
        or "response.custom_tool_call_input.delta" or "response.custom_tool_call_input.done"
        or "response.web_search_call.in_progress" or "response.web_search_call.searching" or "response.web_search_call.completed";
}
