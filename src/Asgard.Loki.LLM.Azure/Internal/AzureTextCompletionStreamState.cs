using System.Text.Json;

namespace Asgard.Loki.LLM.Azure.Internal;

/// <summary>原生文本补全按 text/logprobs 追踪 choice，不能借用 Chat delta 状态来判断生成尾部。</summary>
internal sealed class AzureTextCompletionStreamState(int maximumChoices)
{
    private readonly HashSet<int> _seen = [];
    private readonly Dictionary<int, string> _finished = [];
    private string? _id;
    private string? _model;
    /// <summary>第一个 choice 结束后整段原生尾部暂存，不能只留一条结束帧。</summary>
    internal bool Finishing => _finished.Count != 0;
    /// <summary>逐帧校验原生文本和关联，不组装文本或重写结束原因。</summary>
    internal void Observe(JsonElement data)
    {
        if (!data.TryGetProperty("choices", out var choices)) return;
        if (choices.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Azure 文本补全 SSE choices 必须为数组。");
        Identity(data, "id", ref _id); Identity(data, "model", ref _model);
        if (data.TryGetProperty("object", out var kind) && (kind.ValueKind != JsonValueKind.String || kind.GetString() != "text_completion")) throw new InvalidDataException("Azure 文本补全 SSE 原生类型改变。");
        var frameIndices = new HashSet<int>();
        foreach (var choice in choices.EnumerateArray())
        {
            var index = AzureTextCompletionJson.Index(choice); if (!frameIndices.Add(index)) throw new InvalidDataException("文本补全同帧 choice 索引重复。");
            if (_seen.Add(index) && _seen.Count > maximumChoices) throw new InvalidDataException("文本补全 choice 关联数超限。");
            var generation = AzureTextCompletionJson.Generation(choice); if (_finished.ContainsKey(index) && generation) throw new InvalidDataException("文本补全结束的 choice 之后仍有 text/logprobs 生成数据。");
            if (!choice.TryGetProperty("finish_reason", out var finish) || finish.ValueKind == JsonValueKind.Null) continue;
            if (finish.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(finish.GetString())) throw new InvalidDataException("文本补全原生结束原因无效。"); var reason = finish.GetString()!;
            if (_finished.TryGetValue(index, out var previous) && !(reason == "content_filter" && previous != reason && choice.TryGetProperty("content_filter_results", out var filters) && filters.ValueKind == JsonValueKind.Object)) throw new InvalidDataException("文本补全重复 choice 终态。");
            _finished[index] = reason;
        }
    }
    /// <summary>DONE 不能替代任一已经观察到的 choice 结束原因。</summary>
    internal void Complete() { if (_seen.Count == 0 || _seen.Count != _finished.Count) throw new InvalidDataException("文本补全在所有观察到的 choice 原生终态之前结束。"); }
    /// <summary>空身份注释不覆盖已选身份，非空原生身份不能在流中改变。</summary>
    private static void Identity(JsonElement data, string name, ref string? captured)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return; if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("原生文本补全身份必须是文本。"); var text = value.GetString()!; if (text.Length == 0) return;
        if (captured is not null && captured != text) throw new InvalidDataException("原生文本补全身份在流中改变。"); captured = text;
    }
}
