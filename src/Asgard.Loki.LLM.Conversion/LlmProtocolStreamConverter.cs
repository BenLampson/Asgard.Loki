using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Conversion.Internal;

namespace Asgard.Loki.LLM.Conversion;

/// <summary>一次流专用的显式转换状态；文本即时输出，函数参数完成后输出。</summary>
public sealed class LlmProtocolStreamConverter
{
    private readonly SourceStream _source;
    private readonly TargetStream _target;
    private readonly LlmConversionOptions _options;
    private readonly bool _identity;
    private bool _complete;
    private JsonObject _last = new();

    /// <summary>每次调用创建独立转换器，不跨会话共享流状态。</summary>
    public LlmProtocolStreamConverter(LlmProtocol from, LlmProtocol to, LlmConversionOptions? options = null)
    {
        LlmProtocolConverter.Validate(new(), from, to, options);
        _source = new(from); _target = new(to); _options = options ?? new(); _identity = from == to;
    }

    /// <summary>转换中已发现的累计损耗诊断快照。</summary>
    public IReadOnlyList<LlmConversionDiagnostic> Diagnostics => Array.AsReadOnly(_source.Document.Diagnostics.ToArray());

    /// <summary>处理一个原生 JSON 事件，不执行 HTTP 或重新订阅。</summary>
    public IReadOnlyList<LlmConversionResult> Push(JsonObject sourceEvent)
    {
        ArgumentNullException.ThrowIfNull(sourceEvent);
        if (_complete) throw new InvalidOperationException("流转换已结束。");
        _last = sourceEvent.DeepClone().AsObject();
        if (_identity) return [new(sourceEvent, sourceEvent, [])];
        try
        {
            var changes = _source.Push(sourceEvent);
            LlmProtocolConverter.Enforce(_source.Document, _options);
            var events = _target.Push(_source.Document, changes);
            LlmProtocolConverter.Enforce(_source.Document, _options);
            return events.Select(item => new LlmConversionResult(item, sourceEvent, Diagnostics)).ToArray();
        }
        catch { _complete = true; throw; }
    }

    /// <summary>上游枚举完整成功后调用；缺失终态、非法工具 JSON 或损耗均明确失败。</summary>
    public IReadOnlyList<LlmConversionResult> Complete()
    {
        if (_complete) throw new InvalidOperationException("流转换已结束。");
        _complete = true;
        if (_identity) return [];
        var changes = _source.Complete();
        var events = _target.Push(_source.Document, changes);
        events.AddRange(_target.Complete(_source.Document));
        LlmProtocolConverter.Enforce(_source.Document, _options);
        return events.Select(item => new LlmConversionResult(item, _last, Diagnostics)).ToArray();
    }
}
