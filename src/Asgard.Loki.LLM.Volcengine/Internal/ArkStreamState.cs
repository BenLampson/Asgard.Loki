using System.Text.Json;

namespace Asgard.Loki.LLM.Volcengine.Internal;

/// <summary>方舟原生 Chat/Responses 生命周期，choice 结束不是全局 DONE，不组合工具参数或改写响应。</summary>
internal sealed class ArkStreamState(bool responses, int maximumChoices)
{
    private bool _done;
    private bool _terminal;
    private bool _failure;
    private string? _id;
    private readonly Dictionary<long, bool> _choices = [];
    internal bool Finishing { get; private set; }
    /// <summary>原生类型与独立 SSE 名称分层，未知业务对象不参与识别。</summary>
    internal static string? Type(JsonElement data, string? name, bool responses)
    {
        if (!data.TryGetProperty("type", out var type)) return name;
        if (type.ValueKind != JsonValueKind.String) { if (responses) throw new InvalidDataException("方舟 Responses SSE type 需要字符串。"); return name; } return type.GetString();
    }
    /// <summary>原生根错误和 response.failed；未知事件中的业务 error/response 数据不抢占生命周期。</summary>
    internal static bool Failure(JsonElement data, string? name, bool responses)
    {
        var type = Type(data, name, responses);
        return type is "error" or "response.failed" || (string.IsNullOrEmpty(type) || type == "message" && !data.TryGetProperty("type", out _)) && ArkJson.Error(data);
    }
    /// <summary>DONE 后仍读传输以发现迟到错误，但不接受重复 DONE 或后续数据。</summary>
    internal void Done()
    {
        if (_done || responses && !_terminal) throw new InvalidDataException("方舟 DONE 重复或早于原生 Responses 终态。");
        _done = true; Finishing = true;
    }
    /// <summary>完整原生帧的关联和已知终态必须一致，未知尾部保持原样。</summary>
    internal void Observe(JsonElement data, string? name)
    {
        if (_done) throw new InvalidDataException("方舟 DONE 之后出现额外数据。");
        var type = Type(data, name, responses); var failure = Failure(data, name, responses);
        if (responses)
        {
            var status = type switch { "response.completed" => "completed", "response.incomplete" => "incomplete", "response.failed" => "failed", _ => null };
            var known = status is not null || Content(type); var hasResponse = data.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object;
            if (known && hasResponse && response.TryGetProperty("id", out var id)) Identity(id);
            if (known && data.TryGetProperty("response_id", out var responseId)) Identity(responseId);
            if (status is not null || failure)
            {
                if (_terminal) throw new InvalidDataException("方舟 Responses 重复终态。");
                if (status is not null && (!hasResponse || !response.TryGetProperty("id", out var nativeId) || nativeId.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(nativeId.GetString()) || !response.TryGetProperty("status", out var value) || value.ValueKind != JsonValueKind.String || value.GetString() != status))
                    throw new InvalidDataException("方舟 Responses 终态缺少一致的原生 ID/状态。");
                _terminal = true; Finishing = true;
            }
            else if (_terminal && Content(type)) throw new InvalidDataException("方舟 Responses 终态后重新生成。");
        }
        else
        {
            if (failure) { if (_terminal) throw new InvalidDataException("方舟 Chat 重复错误终态。"); _terminal = true; _failure = true; Finishing = true; return; }
            if (!data.TryGetProperty("choices", out var choices)) return;
            if (_terminal) throw new InvalidDataException("方舟 Chat 错误终态之后重新生成。");
            if (choices.ValueKind != JsonValueKind.Array) throw new InvalidDataException("方舟 choices 必须是原生数组。");
            if (data.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null && !(id.ValueKind == JsonValueKind.String && id.GetString()!.Length == 0)) Identity(id);
            HashSet<long> seen = [];
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.ValueKind != JsonValueKind.Object) throw new InvalidDataException("方舟 choice 必须是对象。");
                long index = 0;
                if (choice.TryGetProperty("index", out var native)) { if (!native.TryGetInt64(out index) || index < 0) throw new InvalidDataException("方舟 choice 索引无效。"); }
                else if (choices.GetArrayLength() != 1) throw new InvalidDataException("方舟多项 choice 不能省略关联索引。");
                if (!seen.Add(index)) throw new InvalidDataException("方舟同帧 choice 索引重复。");
                if (!_choices.ContainsKey(index)) { if (_choices.Count >= maximumChoices) throw new InvalidDataException("方舟 choice 关联数超限。"); _choices.Add(index, false); }
                var finish = choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null;
                if (finish && (reason.ValueKind != JsonValueKind.String || reason.GetString()!.Length == 0)) throw new InvalidDataException("方舟 finish_reason 无效。");
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) throw new InvalidDataException("方舟原生 choice.delta 必须是对象/null。");
                var generates = delta.ValueKind == JsonValueKind.Object && delta.EnumerateObject().Any();
                if (_choices[index] && (finish || generates)) throw new InvalidDataException("方舟已结束 choice 重复结束或继续生成。");
                if (finish) { _choices[index] = true; Finishing = true; }
            }
        }
    }
    /// <summary>Chat 需真实 DONE 或原生错误，Responses 需自己的终态，绝不从 EOF 合成。</summary>
    internal void Complete()
    { if (responses ? !_terminal : !_done && !_failure) throw new InvalidDataException("方舟原生流在协议全局终态前断开。"); }
    private void Identity(JsonElement id)
    {
        if (id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString()) || _id is not null && _id != id.GetString()) throw new InvalidDataException("方舟原生响应 ID 无效或变化。"); _id = id.GetString();
    }
    private static bool Content(string? type) => type is "response.created" or "response.in_progress" or "response.queued"
        or "response.output_item.added" or "response.output_item.done" or "response.content_part.added" or "response.content_part.done"
        or "response.output_text.delta" or "response.output_text.done" or "response.output_text.annotation.added"
        or "response.function_call_arguments.delta" or "response.function_call_arguments.done"
        or "response.reasoning_summary_part.added" or "response.reasoning_summary_part.done" or "response.reasoning_summary_text.delta" or "response.reasoning_summary_text.done"
        or "response.reasoning_text.delta" or "response.reasoning_text.done";
}
