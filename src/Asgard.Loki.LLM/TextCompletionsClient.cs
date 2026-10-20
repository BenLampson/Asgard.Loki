using Asgard.Loki.LLM.Internal;

namespace Asgard.Loki.LLM;

/// <summary>原生文本补全只复用完整目标绑定，协议正文和流式状态仍归供应商所有。</summary>
internal sealed class TextCompletionsClient(LlmInvocation invocation) : ITextCompletionsClient
{
    /// <inheritdoc />
    public async Task<TextCompletion> CompleteAsync(TextCompletionRequest request, LlmCallOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var payload = Snapshot(request, false);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.TextCompletions, options, null, cancellationToken).ConfigureAwait(false);
        payload["model"] = call.Context.Target.UpstreamModel; cancellationToken.ThrowIfCancellationRequested();
        var result = await ((ITextCompletionsAdapter)call.Provider).CompleteAsync(payload, call.Context, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); return result;
    }
    /// <inheritdoc />
    public async IAsyncEnumerable<TextCompletionEvent> StreamAsync(TextCompletionRequest request, LlmCallOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var payload = Snapshot(request, true);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.TextCompletions, options, null, cancellationToken).ConfigureAwait(false);
        payload["model"] = call.Context.Target.UpstreamModel; cancellationToken.ThrowIfCancellationRequested();
        await foreach (var item in ((ITextCompletionsAdapter)call.Provider).StreamAsync(payload, call.Context, cancellationToken).ConfigureAwait(false))
        { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        cancellationToken.ThrowIfCancellationRequested();
    }
    /// <summary>首次缓存等待前复制完整原生参数，方法独占模型与流式控制；不填充或改写 prompt。</summary>
    private static JsonObject Snapshot(TextCompletionRequest request, bool stream)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.Parameters.Any(p => p.Key.Equals("model", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("stream", StringComparison.OrdinalIgnoreCase)
            || p.Value.ValueKind == JsonValueKind.Undefined)) throw new ArgumentException("文本补全扩展参数不能覆盖 model/stream 或包含 Undefined。");
        LlmJsonInput.Request(request.Model, null, request.Parameters);
        return LlmInvocation.Payload(request, request.Model, stream);
    }
}
