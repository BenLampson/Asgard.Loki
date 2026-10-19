using System.Text.Json;

namespace Asgard.Loki.LLM.AWS.Internal;

/// <summary>Nova 独立消息/普通工具状态；允许文本/推理隐式开始，其他原生分支保持原形。</summary>
internal sealed class AwsNovaStreamState(int maximumBlocks)
{
    private readonly HashSet<int> _seen = [];
    private readonly Dictionary<int, string> _open = [];
    private readonly HashSet<string> _toolIds = new(StringComparer.Ordinal);
    private bool _started;
    private bool _stopped;
    private bool _metadata;
    /// <summary>messageStop 后的所有原生帧暂存到 metadata、EOF 和释放均成功。</summary>
    internal bool Finishing => _stopped;
    /// <summary>只解释已知事件及关联字段，未知事件/值保留而不充当成功标记。</summary>
    internal void Observe(string type, JsonElement data)
    {
        switch (type)
        {
            case "messageStart":
                if (_started || _stopped) throw Invalid(); Text(data, "role"); _started = true; break;
            case "contentBlockStart":
                RequireContent(); var startIndex = Index(data); if (_seen.Contains(startIndex)) throw Invalid(); var start = Object(data, "start"); Union(start, "toolUse", "text", "reasoningContent"); var startKind = "unknown";
                if (start.TryGetProperty("toolUse", out _))
                {
                    var tool = Object(start, "toolUse"); var id = Text(tool, "toolUseId"); Text(tool, "name"); if (!_toolIds.Add(id)) throw Invalid(); startKind = "toolUse";
                }
                Open(startIndex, startKind); break;
            case "contentBlockDelta":
                RequireContent(); var deltaIndex = Index(data); var deltaKind = DeltaKind(Object(data, "delta"));
                if (!_open.TryGetValue(deltaIndex, out var original))
                {
                    if (_seen.Contains(deltaIndex) || deltaKind == "toolUse") throw Invalid(); Open(deltaIndex, deltaKind);
                }
                else if (original == "unknown") { if (deltaKind == "toolUse") throw Invalid(); _open[deltaIndex] = deltaKind; }
                else if (deltaKind != "unknown" && deltaKind != original) throw Invalid();
                break;
            case "contentBlockStop":
                RequireContent(); if (!_open.Remove(Index(data))) throw Invalid(); break;
            case "messageStop":
                RequireContent(); if (_open.Count != 0) throw Invalid(); Text(data, "stopReason"); _stopped = true; break;
            case "metadata":
                if (!_stopped || _metadata) throw Invalid(); Object(data, "usage"); if (data.TryGetProperty("metrics", out _)) Object(data, "metrics"); _metadata = true; break;
        }
    }
    /// <summary>正常结束必须包括原生消息结束和用量尾帧，存在的指标对象也必须有效，不由 EOF 合成。</summary>
    internal void Complete()
    {
        if (!_started || !_stopped || !_metadata || _open.Count != 0) throw Invalid();
    }
    /// <summary>按原始索引有限跟踪，不因稀疏的大索引分配数组。</summary>
    private void Open(int index, string kind)
    {
        if (_seen.Count >= maximumBlocks || !_seen.Add(index)) throw Invalid(); _open.Add(index, kind);
    }
    /// <summary>元数据可在终态后到达，内容和消息事件不能重新开启生成。</summary>
    private void RequireContent()
    {
        if (!_started || _stopped) throw Invalid();
    }
    /// <summary>识别已知联合分支以检查类型关联；引用属于文本块，未知分支不被重写。</summary>
    private static string DeltaKind(JsonElement delta)
    {
        Union(delta, "text", "reasoningContent", "toolUse");
        if (delta.TryGetProperty("text", out var text)) { if (text.ValueKind != JsonValueKind.String) throw Invalid(); return "text"; }
        if (delta.TryGetProperty("reasoningContent", out _))
        {
            var reasoning = Object(delta, "reasoningContent"); if (reasoning.TryGetProperty("text", out var textPart) && textPart.ValueKind != JsonValueKind.String) throw Invalid(); return "reasoning";
        }
        if (delta.TryGetProperty("toolUse", out _)) { var tool = Object(delta, "toolUse"); if (tool.TryGetProperty("input", out var input) && input.ValueKind != JsonValueKind.String) throw Invalid(); return "toolUse"; }
        return "unknown";
    }
    /// <summary>原始非负数字必须精确为 int32，不通过浮点数舍入关联。</summary>
    private static int Index(JsonElement data)
    {
        if (!data.TryGetProperty("contentBlockIndex", out var index) || index.ValueKind != JsonValueKind.Number) throw Invalid(); return AwsInteger.Read(index);
    }
    /// <summary>已知对象字段不能由 null 或标量替代。</summary>
    private static JsonElement Object(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : throw Invalid();
    /// <summary>已知关联/角色/终态字段必须是非空原生字符串。</summary>
    private static string Text(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString()) ? value.GetString()! : throw Invalid();
    /// <summary>已知互斥成员不能同时出现；未知成员继续原样保留。</summary>
    private static void Union(JsonElement data, params string[] names)
    {
        if (names.Count(name => data.TryGetProperty(name, out _)) > 1) throw Invalid();
    }
    /// <summary>固定错误不回显原生内容或工具参数。</summary>
    private static InvalidDataException Invalid() => new("Bedrock NovaStream 消息、内容块/工具关联或终态顺序无效或不完整。");
}
