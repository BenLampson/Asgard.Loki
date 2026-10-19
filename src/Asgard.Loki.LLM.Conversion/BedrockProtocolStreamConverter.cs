using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Conversion.Internal;

namespace Asgard.Loki.LLM.Conversion;

/// <summary>单次 Nova/Converse 逻辑事件转换，不创建传输帧、连接、重订阅或成功终态。</summary>
public sealed class BedrockProtocolStreamConverter
{
    private static readonly string[] Events = ["messageStart", "contentBlockStart", "contentBlockDelta", "contentBlockStop", "messageStop", "metadata"];
    private static readonly string[] Errors = ["error", "exception", "internalServerException", "modelStreamErrorException", "throttlingException", "validationException", "serviceUnavailableException", "modelTimeoutException"];
    private readonly LlmProtocol _from;
    private readonly LlmProtocol _to;
    private readonly LlmConversionOptions _options;
    private readonly LlmConversionContext? _context;
    private readonly BedrockStreamFields _fields;
    private readonly BedrockStreamState _state;
    private readonly List<BedrockStreamConversionResult> _tail = [];
    private long _tailBytes;
    private bool _complete;

    /// <summary>每次枚举独立建立状态，原生协议明确选择，不读取模型名。</summary>
    public BedrockProtocolStreamConverter(LlmProtocol from, LlmProtocol to, LlmConversionOptions? options = null)
        : this(from, to, options, null) { }
    /// <summary>反向转换原请求的上游流，校验不可变历史工具 ID 和选项。</summary>
    public BedrockProtocolStreamConverter(LlmConversionContext context)
        : this(Required(context).TargetProtocol, context.SourceProtocol, context.Options, context) { }
    /// <summary>上下文遵循标准空参数验证。</summary>
    private static LlmConversionContext Required(LlmConversionContext context) { ArgumentNullException.ThrowIfNull(context); return context; }
    /// <summary>只允许本原生协议对，默认省略资源策略不改变此前完整 JSON 的绑定字节。</summary>
    private BedrockProtocolStreamConverter(LlmProtocol from, LlmProtocol to, LlmConversionOptions? options, LlmConversionContext? context)
    {
        if (!BedrockConversion.IsBedrock(from) || !BedrockConversion.IsBedrock(to)) throw new ArgumentOutOfRangeException(nameof(from));
        _options = ConversionOptionsSnapshot.Capture(options); LlmProtocolConverter.Validate(new(), from, to, _options);
        _from = from; _to = to; _context = context; _fields = new(_options); _state = new(_options, _fields, context);
    }
    /// <summary>累计诊断的独立只读快照，未知事件即使未产生目标增量也不会静默消失。</summary>
    public IReadOnlyList<LlmConversionDiagnostic> Diagnostics => _fields.Diagnostics;
    /// <summary>Nova 输入完整内层 JSON 信封；Converse 必须提供独立原始事件名。终态仅暂存。</summary>
    public IReadOnlyList<BedrockStreamConversionResult> Push(JsonObject sourceEvent, string? sourceEventName = null)
    {
        ArgumentNullException.ThrowIfNull(sourceEvent); if (_complete) throw new InvalidOperationException("流转换已结束。");
        if (_from == _to)
        {
            var known = Events.Where(sourceEvent.ContainsKey).ToArray();
            var nativeType = _from == LlmProtocol.BedrockNova && known.Length == 1 ? known[0] : sourceEventName;
            return [new(sourceEvent, sourceEvent, nativeType, sourceEventName, [], _context)];
        }
        try
        {
            if (Errors.Contains(sourceEventName, StringComparer.Ordinal) || _from == LlmProtocol.BedrockNova && Errors.Where(name => name is not ("error" or "exception")).Any(sourceEvent.ContainsKey))
                throw new LlmConversionException([new("upstream_stream_error", "$event", "原生上游错误不能转换为成功终态。")]);
            var type = ReadType(sourceEvent, sourceEventName);
            if (type is null) { _fields.Pick(sourceEvent, "$event"); _fields.Loss("$event", "unknown_event"); _fields.Enforce(); return []; }
            JsonObject payload;
            if (_from == LlmProtocol.BedrockNova)
            { _fields.Pick(sourceEvent, "$event", type); payload = sourceEvent[type] as JsonObject ?? throw new InvalidDataException("Nova 原生事件载荷必须是对象。"); }
            else payload = sourceEvent;
            var mapped = _state.Map(type, payload, _from == LlmProtocol.BedrockNova ? "$event." + type : "$event"); _fields.Enforce();
            var target = _to == LlmProtocol.BedrockNova ? new JsonObject { [type] = mapped } : mapped;
            var result = new BedrockStreamConversionResult(target, sourceEvent, type, sourceEventName, Diagnostics, _context);
            if (!_state.Finishing) return [result];
            _tailBytes += Encoding.UTF8.GetByteCount(result.Data.GetRawText()) + (long)Encoding.UTF8.GetByteCount(result.OriginalData.GetRawText());
            if (_tailBytes > (_options.MaxBedrockStreamTailBytes ?? 1024 * 1024)) BedrockConversion.Reject("$event", "bedrock_stream_tail_limit");
            _tail.Add(result); return [];
        }
        catch { _complete = true; _tail.Clear(); throw; }
    }
    /// <summary>调用者只应在源正常枚举、异步释放和最终取消校验都成功之后调用此方法。</summary>
    public IReadOnlyList<BedrockStreamConversionResult> Complete()
    {
        if (_complete) throw new InvalidOperationException("流转换已结束。"); _complete = true;
        try
        {
            if (_from == _to) return [];
            _state.Complete(); _fields.Enforce();
            return _tail.Select(item => new BedrockStreamConversionResult(Parse(item.Data), Parse(item.OriginalData), item.EventType,
                item.SourceEventName, Diagnostics, _context)).ToArray();
        }
        finally { _tail.Clear(); }
    }
    /// <summary>Converse 不读取载荷 type；Nova 只识别明确的信封联合成员，歧义联合硬失败。</summary>
    private string? ReadType(JsonObject source, string? name)
    {
        if (_from == LlmProtocol.BedrockConverse)
        { ArgumentException.ThrowIfNullOrWhiteSpace(name); return Events.Contains(name, StringComparer.Ordinal) ? name : null; }
        var known = Events.Where(source.ContainsKey).ToArray();
        if (known.Length > 1) throw new InvalidDataException("Nova 流的已知原生联合成员不能同时出现。");
        return known.SingleOrDefault();
    }
    /// <summary>仅从已拍摄快照取得最终结果副本，不重写源事件。</summary>
    private static JsonObject Parse(JsonElement value) => JsonNode.Parse(value.GetRawText())!.AsObject();
}
