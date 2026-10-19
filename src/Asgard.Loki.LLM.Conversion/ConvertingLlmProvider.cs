using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Conversion.Internal;

namespace Asgard.Loki.LLM.Conversion;

/// <summary>由调用者显式注册的单上游协议装饰器，不重选目标或执行工具。</summary>
public sealed class ConvertingLlmProvider : IChatCompletionsAdapter, IResponsesAdapter, IClaudeMessagesAdapter, IGeminiAdapter
{
    private readonly ILlmProvider _upstream;
    private readonly LlmProtocol _protocol;
    private readonly LlmConversionOptions _options;

    /// <summary>固定原生供应商、上游协议与转换策略，绑定身份包含这些配置。</summary>
    public ConvertingLlmProvider(ILlmProvider upstream, LlmProtocol upstreamProtocol, string id, LlmConversionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        LlmProtocolConverter.Validate(new(), upstreamProtocol, upstreamProtocol, options);
        if (upstream is ConvertingLlmProvider) throw new ArgumentException("不能嵌套转换供应商。", nameof(upstream));
        if (upstreamProtocol == LlmProtocol.ChatCompletions && upstream is not IChatCompletionsAdapter
            || upstreamProtocol == LlmProtocol.Responses && upstream is not IResponsesAdapter
            || upstreamProtocol == LlmProtocol.ClaudeMessages && upstream is not IClaudeMessagesAdapter
            || upstreamProtocol == LlmProtocol.Gemini && upstream is not IGeminiAdapter)
            throw new ArgumentException("供应商未实现选定的上游原生协议。", nameof(upstream));
        _upstream = upstream; _protocol = upstreamProtocol; _options = options is null ? new() : options with { }; Id = id;
    }

    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity => $"{GetType().AssemblyQualifiedName}:conversion-v5:{_upstream.Id}:{_upstream.BindingIdentity}:{_protocol}:{JsonSerializer.Serialize(_options)}";

    /// <inheritdoc />
    public async Task<ChatCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        var result = await CompleteAsync(request, LlmProtocol.ChatCompletions, context, cancellationToken).ConfigureAwait(false);
        return new(result.Data, result.Metadata);
    }
    /// <inheritdoc />
    public async Task<Response> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        var result = await CompleteAsync(request, LlmProtocol.Responses, context, cancellationToken).ConfigureAwait(false);
        return new(result.Data, result.Metadata);
    }
    /// <summary>显式区分同名的 Claude 创建方法。</summary>
    async Task<ClaudeMessage> IClaudeMessagesAdapter.CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
    {
        var result = await CompleteAsync(request, LlmProtocol.ClaudeMessages, context, cancellationToken).ConfigureAwait(false);
        return new(result.Data, result.Metadata);
    }
    /// <inheritdoc />
    public async Task<GeminiContent> GenerateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        var result = await CompleteAsync(request, LlmProtocol.Gemini, context, cancellationToken).ConfigureAwait(false);
        return new(result.Data, result.Metadata);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in StreamAsync(request, LlmProtocol.ChatCompletions, context, cancellationToken).ConfigureAwait(false))
            yield return new(item.Data, item.Metadata, item.EventName, item.EventId, item.RetryMilliseconds);
    }
    /// <summary>显式区分 Responses 原生事件。</summary>
    async IAsyncEnumerable<ResponseEvent> IResponsesAdapter.StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in StreamAsync(request, LlmProtocol.Responses, context, cancellationToken).ConfigureAwait(false))
            yield return new(item.Data, item.Metadata, item.EventName, item.EventId, item.RetryMilliseconds);
    }
    /// <summary>显式区分 Claude 原生事件。</summary>
    async IAsyncEnumerable<ClaudeMessageEvent> IClaudeMessagesAdapter.StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in StreamAsync(request, LlmProtocol.ClaudeMessages, context, cancellationToken).ConfigureAwait(false))
            yield return new(item.Data, item.Metadata, item.EventName, item.EventId, item.RetryMilliseconds);
    }
    /// <summary>显式区分 Gemini 原生事件。</summary>
    async IAsyncEnumerable<GeminiContentEvent> IGeminiAdapter.StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in StreamAsync(request, LlmProtocol.Gemini, context, cancellationToken).ConfigureAwait(false))
            yield return new(item.Data, item.Metadata, item.EventName, item.EventId, item.RetryMilliseconds);
    }

    /// <summary>请求和响应分别转换；不把上游 JSON 改写后冒充原生结果。</summary>
    private async Task<LlmJsonResult> CompleteAsync(JsonObject request, LlmProtocol source, LlmAdapterContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var converted = LlmProtocolConverter.ConvertRequest(request, source, _protocol, _options);
        var payload = JsonNode.Parse(converted.Data.GetRawText())!.AsObject();
        LlmJsonResult raw = _protocol switch
        {
            LlmProtocol.ChatCompletions => await ((IChatCompletionsAdapter)_upstream).CompleteAsync(payload, context, cancellationToken).ConfigureAwait(false),
            LlmProtocol.Responses => await ((IResponsesAdapter)_upstream).CreateAsync(payload, context, cancellationToken).ConfigureAwait(false),
            LlmProtocol.ClaudeMessages => await ((IClaudeMessagesAdapter)_upstream).CreateAsync(payload, context, cancellationToken).ConfigureAwait(false),
            _ => await ((IGeminiAdapter)_upstream).GenerateAsync(payload, context, cancellationToken).ConfigureAwait(false)
        };
        if (source == _protocol) return raw;
        var result = LlmProtocolConverter.ConvertResponse(JsonNode.Parse(raw.Data.GetRawText())!.AsObject(), _protocol, source, _options);
        var metadata = context.Metadata with { Conversion = new(_protocol, source, raw.Data, converted.Diagnostics.Concat(result.Diagnostics)) };
        return new ChatCompletion(result.Data, metadata);
    }

    /// <summary>原生分支完整透传事件；跨协议分支独立维护本次流状态。</summary>
    private async IAsyncEnumerable<LlmJsonEvent> StreamAsync(JsonObject request, LlmProtocol source, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var converted = LlmProtocolConverter.ConvertRequest(request, source, _protocol, _options);
        var payload = JsonNode.Parse(converted.Data.GetRawText())!.AsObject();
        var converter = new LlmProtocolStreamConverter(_protocol, source, _options);
        await foreach (var raw in UpstreamAsync(payload, context, cancellationToken).ConfigureAwait(false))
        {
            if (source == _protocol) { yield return raw; continue; }
            foreach (var item in converter.Push(JsonNode.Parse(raw.Data.GetRawText())!.AsObject()))
                yield return Wrap(item, converted.Diagnostics, source, context);
        }
        if (source != _protocol)
            foreach (var item in converter.Complete()) yield return Wrap(item, converted.Diagnostics, source, context);
    }

    /// <summary>仅调用构造时固定的原生协议适配器。</summary>
    private async IAsyncEnumerable<LlmJsonEvent> UpstreamAsync(JsonObject payload, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        switch (_protocol)
        {
            case LlmProtocol.ChatCompletions:
                await foreach (var item in ((IChatCompletionsAdapter)_upstream).StreamAsync(payload, context, cancellationToken).ConfigureAwait(false)) yield return item;
                break;
            case LlmProtocol.Responses:
                await foreach (var item in ((IResponsesAdapter)_upstream).StreamAsync(payload, context, cancellationToken).ConfigureAwait(false)) yield return item;
                break;
            case LlmProtocol.ClaudeMessages:
                await foreach (var item in ((IClaudeMessagesAdapter)_upstream).StreamAsync(payload, context, cancellationToken).ConfigureAwait(false)) yield return item;
                break;
            case LlmProtocol.Gemini:
                await foreach (var item in ((IGeminiAdapter)_upstream).StreamAsync(payload, context, cancellationToken).ConfigureAwait(false)) yield return item;
                break;
        }
    }

    /// <summary>为转换事件附加上游原文；不冒用原始 SSE ID 或重连提示。</summary>
    private LlmJsonEvent Wrap(LlmConversionResult result, IEnumerable<LlmConversionDiagnostic> requestDiagnostics, LlmProtocol source, LlmAdapterContext context)
    {
        var metadata = context.Metadata with { Conversion = new(_protocol, source, result.OriginalData, requestDiagnostics.Concat(result.Diagnostics)) };
        return new ChatCompletionEvent(result.Data, metadata,
            result.Data.TryGetProperty("type", out var type) ? type.GetString() : null);
    }
}
