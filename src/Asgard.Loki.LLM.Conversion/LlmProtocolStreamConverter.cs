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
    private readonly LlmConversionContext? _context;
    private bool _complete;
    private JsonObject _last = new();

    /// <summary>每次调用创建独立转换器，不跨会话共享流状态。</summary>
    public LlmProtocolStreamConverter(LlmProtocol from, LlmProtocol to, LlmConversionOptions? options = null)
        : this(from, to, options, null) { }

    /// <summary>反向转换原请求对应的上游流；上下文不可变，流状态仍由每个转换器独立持有。</summary>
    public LlmProtocolStreamConverter(LlmConversionContext context)
        : this(RequireContext(context).TargetProtocol, context.SourceProtocol, context.Options, context) { }

    /// <summary>构造器链也统一采用标准空参数验证，不改变上下文或创建替代映射。</summary>
    private static LlmConversionContext RequireContext(LlmConversionContext context)
    {
        ArgumentNullException.ThrowIfNull(context); return context;
    }

    /// <summary>统一建立本次流的协议状态，只有显式工具上下文才增加映射专用资源限制。</summary>
    private LlmProtocolStreamConverter(LlmProtocol from, LlmProtocol to, LlmConversionOptions? options, LlmConversionContext? context)
    {
        options = ConversionOptionsSnapshot.Capture(options);
        LlmProtocolConverter.Validate(new(), from, to, options);
        var mapped = context?.MappedToolCount > 0;
        var concatenate = options.GeminiGroundingStreamMode == GeminiGroundingStreamMode.ConcatenatedTextParts;
        if (concatenate && from != to && (from != LlmProtocol.Responses || to != LlmProtocol.Gemini))
            throw new ArgumentException("文本块拼接引用流只适用于 Responses 到 Gemini，不声明 Gemini 源 part 的所属约定。", nameof(options));
        var grounding = from == LlmProtocol.Gemini && to == LlmProtocol.Responses
            && options.GeminiGroundingStreamMode == GeminiGroundingStreamMode.SingleTextPart;
        var citations = from == LlmProtocol.Responses && to == LlmProtocol.Gemini
            && options.GeminiGroundingStreamMode is GeminiGroundingStreamMode.SingleTextPart or GeminiGroundingStreamMode.ConcatenatedTextParts;
        if ((grounding || citations) && options.OpenAiCitationIndexUnit == LlmCitationIndexUnit.Unspecified)
            throw new ArgumentException("Gemini 引用流映射必须显式声明 OpenAI 端点索引单位。", nameof(options));
        _source = new(from, mapped ? 6L * context!.Options.MaxCustomToolInputBytes + 64 : null,
            mapped ? context!.Options.MaxToolConversionOutputCalls : null,
            options.OpenAiCitationIndexUnit, options.MaxStreamAnnotations, options.MaxStreamAnnotationBytes, grounding, citations, concatenate);
        _target = new(to, mapped ? context : null, citations, options.MaxStreamAnnotations, options.MaxStreamAnnotationBytes, concatenate); _options = options; _identity = from == to; _context = context;
    }

    /// <summary>转换中已发现的累计损耗诊断快照。</summary>
    public IReadOnlyList<LlmConversionDiagnostic> Diagnostics => Array.AsReadOnly(_source.Document.Diagnostics.ToArray());

    /// <summary>处理一个原生 JSON 事件，不执行 HTTP 或重新订阅。</summary>
    public IReadOnlyList<LlmConversionResult> Push(JsonObject sourceEvent)
    {
        ArgumentNullException.ThrowIfNull(sourceEvent);
        if (_complete) throw new InvalidOperationException("流转换已结束。");
        _last = sourceEvent.DeepClone().AsObject();
        if (_identity) return [new(sourceEvent, sourceEvent, [], _context)];
        try
        {
            var changes = _source.Push(sourceEvent);
            LlmProtocolConverter.Enforce(_source.Document, _options);
            var events = _target.Push(_source.Document, changes);
            LlmProtocolConverter.Enforce(_source.Document, _options);
            return events.Select(item => new LlmConversionResult(item, sourceEvent, Diagnostics, _context)).ToArray();
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
        return events.Select(item => new LlmConversionResult(item, _last, Diagnostics, _context)).ToArray();
    }
}
