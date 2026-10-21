namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>区分历史调用和待回填调用，禁止重复 ID、结果复用或按名称错配。</summary>
internal sealed class ToolCallTracker
{
    private readonly HashSet<string> _all = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);

    /// <summary>尚有结果未回填，目标协议不能提前开始下一轮普通消息。</summary>
    internal bool HasPending => _pending.Count != 0;

    /// <summary>登记唯一的原始调用身份和函数名称。</summary>
    internal void Register(string id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!_all.Add(id)) throw new ArgumentException("工具调用 ID 重复。");
        _pending.Add(id, name);
    }
    /// <summary>只允许待回填调用消费一次结果，返回原始名称。</summary>
    internal string Consume(string id, string? name = null)
    {
        if (!_pending.TryGetValue(id, out var original) || name is not null && name != original)
            throw new ArgumentException("工具结果没有匹配的待回填调用，或已经回填。");
        _pending.Remove(id);
        return original;
    }
    /// <summary>旧 Gemini 结果没有 ID 时，只能选择唯一的同名待回填调用。</summary>
    internal string Resolve(string? id, string name)
    {
        if (id is null)
        {
            var matches = _pending.Where(p => p.Value == name).Select(p => p.Key).ToArray();
            if (matches.Length != 1) throw new ArgumentException("Gemini 工具结果无法唯一关联待回填调用，请提供 ID。");
            id = matches[0];
        }
        Consume(id, name);
        return id;
    }
    /// <summary>检查生成的标识是否与已有原始标识冲突。</summary>
    internal bool Contains(string id) => _all.Contains(id);
}
