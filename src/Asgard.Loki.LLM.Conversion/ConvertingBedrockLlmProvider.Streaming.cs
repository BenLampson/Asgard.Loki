using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion;

/// <summary>原生传输与纯逻辑事件映射独立，只有完整源枚举和释放成功后才发布转换终态。</summary>
public sealed partial class ConvertingBedrockLlmProvider
{
    /// <summary>Converse 目标保留逻辑事件名，转换结果不声称具有原始二进制帧。</summary>
    async IAsyncEnumerable<BedrockConverseEvent> IBedrockConverseAdapter.StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in Stream(request, LlmProtocol.BedrockConverse, context, cancellationToken).ConfigureAwait(false))
            yield return (BedrockConverseEvent)item;
    }
    /// <summary>Nova 目标使用完整内层信封和明确类型，不伪造外层 chunk/CRC/Base64 包装。</summary>
    async IAsyncEnumerable<BedrockNovaEvent> IBedrockNovaAdapter.StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in Stream(request, LlmProtocol.BedrockNova, context, cancellationToken).ConfigureAwait(false))
            yield return (BedrockNovaEvent)item;
    }
    /// <summary>请求先完成显式映射，原生身份流仍返回原始事件实例及其供应商附件。</summary>
    private async IAsyncEnumerable<LlmJsonEvent> Stream(JsonObject request, LlmProtocol source, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var converted = LlmProtocolConverter.ConvertRequest(request, source, _protocol, _options);
        var payload = Parse(converted.Data); var converter = source == _protocol ? null : new BedrockProtocolStreamConverter(converted.Context!);
        await foreach (var raw in Upstream(payload, context, token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            if (converter is null) { yield return raw; continue; }
            foreach (var item in converter.Push(Parse(raw.Data), raw.EventName))
            { token.ThrowIfCancellationRequested(); yield return Wrap(item, converted.Diagnostics, source, context); }
        }
        token.ThrowIfCancellationRequested();
        if (converter is not null) foreach (var item in converter.Complete())
        { token.ThrowIfCancellationRequested(); yield return Wrap(item, converted.Diagnostics, source, context); }
    }
    /// <summary>固定原生适配器仅枚举一次，不重发、不降级、不重选目标。</summary>
    private async IAsyncEnumerable<LlmJsonEvent> Upstream(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken token)
    {
        if (_protocol == LlmProtocol.BedrockNova)
        { await foreach (var item in ((IBedrockNovaAdapter)_upstream).StreamAsync(request, context, token).ConfigureAwait(false)) yield return item; }
        else
        { await foreach (var item in ((IBedrockConverseAdapter)_upstream).StreamAsync(request, context, token).ConfigureAwait(false)) yield return item; }
    }
    /// <summary>保留完整源 JSON 和损耗诊断；转换事件只暴露目标逻辑类型，不冒用源传输附件。</summary>
    private LlmJsonEvent Wrap(BedrockStreamConversionResult result, IEnumerable<LlmConversionDiagnostic> requestDiagnostics,
        LlmProtocol source, LlmAdapterContext context)
    {
        var metadata = context.Metadata with { Conversion = new(_protocol, source, result.OriginalData, requestDiagnostics.Concat(result.Diagnostics)) };
        return source == LlmProtocol.BedrockNova ? new BedrockNovaEvent(result.Data, metadata, nativeType: result.EventType)
            : new BedrockConverseEvent(result.Data, metadata, result.EventType);
    }
}
