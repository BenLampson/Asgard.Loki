using System.Text.Json;

namespace Asgard.Loki.LLM.Azure.Internal;

/// <summary>Chat 原生 choice 终态及 Azure 异步过滤尾帧；不聚合或重写工具增量。</summary>
internal sealed class AzureChatStreamState(int maximumChoices)
{
    private readonly HashSet<int> _seen = [];
    private readonly Dictionary<int, string> _finished = [];
    private string? _id;
    private string? _model;
    /// <summary>第一次 finish 后暂存所有剩余帧，避免多 choice/过滤尾部提前成功。</summary>
    internal bool Finishing => _finished.Count != 0;
    /// <summary>忽略 Azure 注释空身份，非空原生身份必须一致。</summary>
    internal void Observe(JsonElement data)
    {
        if (!data.TryGetProperty("choices", out var choices)) return;
        if (choices.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Azure Chat SSE 原生 choices 必须是数组。");
        Identity(data, "id", ref _id); Identity(data, "model", ref _model);
        var frameIndices = new HashSet<int>();
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("index", out var indexValue) || indexValue.ValueKind != JsonValueKind.Number || !indexValue.TryGetInt32(out var index) || index < 0 || !frameIndices.Add(index)) throw new InvalidDataException("Azure Chat SSE choice 索引无效或重复。");
            if (_seen.Add(index) && _seen.Count > maximumChoices) throw new InvalidDataException("Azure Chat SSE choice 关联数超限。");
            var generation = choice.TryGetProperty("delta", out var delta) && HasGeneration(delta);
            if (_finished.ContainsKey(index) && generation) throw new InvalidDataException("Azure Chat 在已结束 choice 后追加生成数据。");
            if (!choice.TryGetProperty("finish_reason", out var finish) || finish.ValueKind == JsonValueKind.Null) continue;
            if (finish.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(finish.GetString())) throw new InvalidDataException("Azure Chat finish_reason 无效。");
            var reason = finish.GetString()!;
            if (_finished.ContainsKey(index) && !(reason == "content_filter" && _finished[index] != reason && choice.TryGetProperty("content_filter_results", out _))) throw new InvalidDataException("Azure Chat 重复 choice 终态。");
            _finished[index] = reason;
        }
    }
    /// <summary>DONE 不能替代缺失 choice 的原生结束原因。</summary>
    internal void Complete()
    {
        if (_seen.Count == 0 || _seen.Count != _finished.Count) throw new InvalidDataException("Azure Chat 在完整 choice 终态之前结束。");
    }
    /// <summary>注释可不带 delta 或仅有空字段；原生新内容不可在 finish 后继续。</summary>
    private static bool HasGeneration(JsonElement delta)
    {
        if (delta.ValueKind == JsonValueKind.Null) return false;
        if (delta.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Azure Chat delta 必须是对象或 null。");
        return delta.EnumerateObject().Any(property => property.Value.ValueKind switch
        {
            JsonValueKind.Null => false, JsonValueKind.String => property.Value.GetString()!.Length != 0,
            JsonValueKind.Array => property.Value.GetArrayLength() != 0, JsonValueKind.Object => property.Value.EnumerateObject().Any(), _ => true
        });
    }
    /// <summary>实际服务返回的模型不是部署别名，不强行替换为配置模型。</summary>
    private static void Identity(JsonElement data, string name, ref string? captured)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Azure Chat 身份字段必须是字符串。");
        var text = value.GetString()!; if (text.Length == 0) return;
        if (captured is not null && captured != text) throw new InvalidDataException("Azure Chat 原生身份在流中改变。"); captured = text;
    }
}
