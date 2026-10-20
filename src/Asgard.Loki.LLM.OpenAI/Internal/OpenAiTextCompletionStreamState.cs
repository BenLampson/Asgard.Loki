using System.Text.Json;

namespace Asgard.Loki.LLM.OpenAI.Internal;

/// <summary>原生文本补全自己的选择关联和 DONE；未知业务事件不会变成 Responses 生命周期。</summary>
internal sealed class OpenAiTextCompletionStreamState(int maximumChoices)
{
    private readonly Dictionary<long, bool> _choices = [];
    private string? _id;
    private string? _model;
    private bool _done;
    private bool _error;
    /// <summary>首个结束原因、原生错误或实际 DONE 开始有界尾部暂存。</summary>
    internal bool Finishing { get; private set; }
    /// <summary>只识别实际原生错误，未知类型中的业务 error 和 Responses 名称不属于文本终态。</summary>
    internal static bool IsFailure(JsonElement data, string? eventName)
    {
        var hasType = data.TryGetProperty("type", out var type); var name = hasType && type.ValueKind == JsonValueKind.String ? type.GetString() : eventName;
        return eventName == "error" || name == "error" || (!hasType && string.IsNullOrEmpty(eventName) || !hasType && eventName == "message") && OpenAiHttpJson.IsError(data);
    }
    /// <summary>选择项可以交错结束；已结束项不能重复终态或追加已知 text/logprobs，未知尾部原样保留。</summary>
    internal void Observe(JsonElement data, string? eventName)
    {
        if (_done) throw new InvalidDataException("文本补全在 DONE 后追加数据。");
        if (IsFailure(data, eventName)) { _error = true; Finishing = true; return; }
        if (!data.TryGetProperty("choices", out var choices)) return;
        if (choices.ValueKind != JsonValueKind.Array) throw new InvalidDataException("文本补全 SSE choices 必须为数组。");
        if (_error && choices.GetArrayLength() != 0) throw new InvalidDataException("文本补全在原生错误后重新生成。");
        Identity(data, "id", ref _id); Identity(data, "model", ref _model); var frameIndices = new HashSet<long>();
        foreach (var choice in choices.EnumerateArray())
        {
            var index = OpenAiTextCompletionJson.Index(choice); if (!frameIndices.Add(index)) throw new InvalidDataException("文本补全同帧索引重复。");
            if (!_choices.ContainsKey(index)) { if (_choices.Count >= maximumChoices) throw new InvalidDataException("文本补全 choice 关联数超过上限。"); _choices.Add(index, false); }
            if (_choices[index] && (OpenAiTextCompletionJson.Generation(choice) || OpenAiTextCompletionJson.Finish(choice))) throw new InvalidDataException("文本补全已结束的 choice 再次生成或结束。");
            _ = OpenAiTextCompletionJson.Generation(choice); if (OpenAiTextCompletionJson.Finish(choice)) { _choices[index] = true; Finishing = true; }
        }
    }
    /// <summary>实际 DONE 不能替代任一已观察到的 choice 结束，随后仍需真正 EOF。</summary>
    internal void ObserveDone()
    {
        if (_done || !_error && (_choices.Count == 0 || _choices.Values.Any(finished => !finished))) throw new InvalidDataException("文本补全 DONE 重复或早于全部原生 choice 结束。"); _done = true; Finishing = true;
    }
    /// <summary>EOF 本身不合成 DONE；原生错误可以独立结束，原文仍按资源边界交付。</summary>
    internal void Complete() { if (!_done && !_error) throw new InvalidDataException("文本补全缺少原生 DONE。"); }
    /// <summary>只有真正含 choices 的帧参加生成身份校验；未知业务 ID 保持数据含义。</summary>
    private static void Identity(JsonElement data, string name, ref string? captured)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("文本补全原生身份必须为文本。"); var text = value.GetString()!; if (text.Length == 0) return;
        if (captured is not null && captured != text) throw new InvalidDataException("文本补全原生身份在流中改变。"); captured = text;
    }
}
