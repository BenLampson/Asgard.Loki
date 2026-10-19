using System.Text.Json;

namespace Asgard.Loki.LLM.GoogleVertex.Internal;

/// <summary>MaaS Chat 的原生 choice/tool 索引关联与终态验证，不聚合或改写工具参数和推理增量。</summary>
internal sealed class VertexMaaSStreamState(int maximumChoices, int maximumTools)
{
    private readonly HashSet<int> _seen = [];
    private readonly HashSet<int> _finished = [];
    private readonly Dictionary<(int Choice, int Tool), string?> _tools = [];
    private readonly Dictionary<(int Choice, string Id), int> _ids = [];
    private string? _id;
    private string? _model;
    /// <summary>首个 finish 后必须暂存全部后续事件，包括剩余 choice 和原生用量。</summary>
    internal bool Finishing => _finished.Count != 0;
    /// <summary>保留未知帧与原始 JSON；已知身份及索引关联不得漂移。</summary>
    internal void Observe(JsonElement data)
    {
        Identity(data, "id", ref _id); Identity(data, "model", ref _model);
        if (!data.TryGetProperty("choices", out var choices)) return;
        if (choices.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Vertex MaaS choices 必须是数组。"); var frameIndices = new HashSet<int>();
        foreach (var choice in choices.EnumerateArray())
        {
            var index = Index(choice); if (!frameIndices.Add(index)) throw new InvalidDataException("Vertex MaaS 帧内 choice 索引重复。"); if (_seen.Add(index) && _seen.Count > maximumChoices) throw new InvalidDataException("Vertex MaaS choice 关联数超限。");
            if (choice.TryGetProperty("delta", out var delta))
            {
                var generation = HasGeneration(delta); if (_finished.Contains(index) && generation) throw new InvalidDataException("Vertex MaaS 在已结束 choice 后继续生成。");
                if (delta.ValueKind == JsonValueKind.Object && delta.TryGetProperty("tool_calls", out var tools) && tools.ValueKind != JsonValueKind.Null) ObserveTools(index, tools);
            }
            if (!choice.TryGetProperty("finish_reason", out var finish) || finish.ValueKind == JsonValueKind.Null) continue;
            if (finish.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(finish.GetString()) || !_finished.Add(index)) throw new InvalidDataException("Vertex MaaS choice 终态无效或重复。");
            if (_tools.Any(tool => tool.Key.Choice == index && tool.Value is null)) throw new InvalidDataException("Vertex MaaS 工具在未提供关联 ID 前结束。");
        }
    }
    /// <summary>DONE 和 EOF 都不能代替缺失的原生 choice finish。</summary>
    internal void Complete()
    {
        if (_seen.Count == 0 || _seen.Count != _finished.Count) throw new InvalidDataException("Vertex MaaS 在完整 choice 终态之前结束。");
    }
    /// <summary>工具关联使用 choice/tool 的原始索引，允许名称/参数字符串分片而不聚合。</summary>
    private void ObserveTools(int choice, JsonElement tools)
    {
        if (tools.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Vertex MaaS tool_calls 必须是数组。"); var frameIndices = new HashSet<int>();
        foreach (var tool in tools.EnumerateArray())
        {
            var index = Index(tool); if (!frameIndices.Add(index)) throw new InvalidDataException("Vertex MaaS 帧内工具索引重复。"); var key = (choice, index);
            if (!_tools.ContainsKey(key)) { if (_tools.Count >= maximumTools) throw new InvalidDataException("Vertex MaaS 工具关联数超限。"); _tools.Add(key, null); }
            if (tool.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null)
            {
                if (id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString())) throw new InvalidDataException("Vertex MaaS 工具 ID 无效。"); var text = id.GetString()!;
                if (_tools[key] is { } previous && previous != text || _ids.TryGetValue((choice, text), out var previousIndex) && previousIndex != index) throw new InvalidDataException("Vertex MaaS 工具 ID 与原始索引关联冲突。"); _tools[key] = text; _ids[(choice, text)] = index;
            }
        }
    }
    /// <summary>原始数字索引必须精确为非负 int32，不接受字符串或浮点舍入。</summary>
    private static int Index(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number) throw new InvalidDataException("Vertex MaaS 索引必须是原生数字。"); return VertexInteger.Read(index);
    }
    /// <summary>空注释可以跟随终态，新的已知或未知非空 delta 不允许重新开启生成。</summary>
    private static bool HasGeneration(JsonElement delta)
    {
        if (delta.ValueKind == JsonValueKind.Null) return false; if (delta.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Vertex MaaS delta 必须是对象或 null。");
        return delta.EnumerateObject().Any(property => property.Value.ValueKind switch { JsonValueKind.Null => false, JsonValueKind.String => property.Value.GetString()!.Length != 0, JsonValueKind.Array => property.Value.GetArrayLength() != 0, JsonValueKind.Object => property.Value.EnumerateObject().Any(), _ => true });
    }
    /// <summary>原生服务返回身份不替换为路由别名；首次非空身份确定后不得改变。</summary>
    private static void Identity(JsonElement data, string name, ref string? captured)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return; if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Vertex MaaS 身份字段必须是字符串。"); var text = value.GetString()!; if (text.Length == 0) return;
        if (captured is not null && captured != text) throw new InvalidDataException("Vertex MaaS 原生身份在流中改变。"); captured = text;
    }
}
