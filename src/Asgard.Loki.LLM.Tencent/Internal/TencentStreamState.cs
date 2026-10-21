using System.Text.Json;

namespace Asgard.Loki.LLM.Tencent.Internal;

/// <summary>混元原生选择生命周期；只验证关联，不重写工具增量、推理、限制状态或尾部元数据。</summary>
internal sealed class TencentStreamState(int maximumChoices)
{
    private readonly HashSet<int> _seen = [];
    private readonly HashSet<int> _finished = [];
    private string? _id;
    private bool _implicitIndex;
    /// <summary>任意选择首次结束后，所有原生记录都必须等待完整 EOF 和资源关闭。</summary>
    internal bool Finishing => _finished.Count != 0;
    /// <summary>Id 是生成身份；Created 可以随原生分片变化，不作为固定时间戳比较。</summary>
    internal void Observe(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("混元 SSE 必须是平铺原生 JSON 对象。");
        if (data.TryGetProperty("Id", out var id))
        {
            if (id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString())) throw new InvalidDataException("混元 SSE Id 必须是非空原生字符串。");
            if (_id is not null && _id != id.GetString()) throw new InvalidDataException("混元 SSE 原生生成身份改变。"); _id = id.GetString();
        }
        if (!data.TryGetProperty("Choices", out var choices)) return;
        if (choices.ValueKind != JsonValueKind.Array) throw new InvalidDataException("混元 SSE Choices 必须为数组。");
        var indices = new HashSet<int>(); var count = choices.GetArrayLength();
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object) throw new InvalidDataException("混元 SSE 选择必须为对象。");
            int index;
            if (!choice.TryGetProperty("Index", out var nativeIndex))
            {
                if (count != 1 || _seen.Any(i => i != 0)) throw new InvalidDataException("缺少 Index 的混元 SSE 多选择关联不明确。");
                index = 0; _implicitIndex = true;
            }
            else if (nativeIndex.ValueKind != JsonValueKind.Number || !nativeIndex.TryGetInt32(out index) || index < 0 || _implicitIndex && index != 0) throw new InvalidDataException("混元 SSE 选择索引无效或与省略索引模式冲突。");
            if (!indices.Add(index)) throw new InvalidDataException("混元 SSE 同帧选择索引重复。");
            if (_seen.Add(index) && _seen.Count > maximumChoices) throw new InvalidDataException("混元 SSE 选择关联数超限。");
            var generation = Generation(choice);
            if (_finished.Contains(index) && generation) throw new InvalidDataException("已结束混元选择后仍有原生正文、推理或工具增量。");
            if (!choice.TryGetProperty("FinishReason", out var finish) || finish.ValueKind == JsonValueKind.Null) continue;
            if (finish.ValueKind != JsonValueKind.String) throw new InvalidDataException("混元原生 FinishReason 必须是字符串或 null。");
            if (finish.GetString()!.Length != 0 && !_finished.Add(index)) throw new InvalidDataException("混元 SSE 同一选择重复结束。");
        }
    }
    /// <summary>EOF 本身不能替代任一已观察选择的原生终态，也不能创建缺失的生成 ID。</summary>
    internal void Complete()
    { if (_id is null || _seen.Count == 0 || _seen.Count != _finished.Count) throw new InvalidDataException("混元 SSE 在所有原生选择结束之前断开。"); }
    /// <summary>只解释已知原生生成字段；函数 Arguments 保持字符串分片，不解析或重组 JSON。</summary>
    private static bool Generation(JsonElement choice)
    {
        if (choice.TryGetProperty("Message", out var message) && message.ValueKind != JsonValueKind.Null) throw new InvalidDataException("混元 SSE 不能用完整 Message 替代 Delta。");
        if (!choice.TryGetProperty("Delta", out var delta) || delta.ValueKind == JsonValueKind.Null) return false;
        if (delta.ValueKind != JsonValueKind.Object) throw new InvalidDataException("混元 SSE Delta 必须为对象或 null。");
        _ = Text(delta, "Role"); var generation = Text(delta, "Content") | Text(delta, "ReasoningContent");
        if (!delta.TryGetProperty("ToolCalls", out var tools) || tools.ValueKind == JsonValueKind.Null) return generation;
        if (tools.ValueKind != JsonValueKind.Array) throw new InvalidDataException("混元 SSE ToolCalls 必须为数组或 null。");
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object) throw new InvalidDataException("混元 SSE 工具分片必须为对象。");
            _ = Text(tool, "Id"); _ = Text(tool, "Type");
            if (!tool.TryGetProperty("Function", out var function) || function.ValueKind == JsonValueKind.Null) continue;
            if (function.ValueKind != JsonValueKind.Object) throw new InvalidDataException("混元 SSE Function 必须为对象。");
            _ = Text(function, "Name"); _ = Text(function, "Arguments");
        }
        return generation || tools.GetArrayLength() != 0;
    }
    /// <summary>已知文本字段可以省略或为 null；未知原生字段完全保留。</summary>
    private static bool Text(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return false;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("混元 SSE 已知文本字段类型无效。"); return value.GetString()!.Length != 0;
    }
}
