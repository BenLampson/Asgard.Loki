using Asgard.Loki.LLM.Internal;

namespace Asgard.Loki.LLM;

/// <summary>原生 Cohere v1 Chat 只复用完整目标绑定，协议正文和流式状态仍归供应商所有。</summary>
internal sealed class CohereChatClient(LlmInvocation invocation) : ICohereChatClient
{
    /// <inheritdoc />
    public async Task<CohereChatCompletion> CompleteAsync(CohereChatRequest request, LlmCallOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var payload = Snapshot(request, false);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.CohereChat, options, null, cancellationToken, request: payload).ConfigureAwait(false);
        payload["model"] = call.Context.Target.UpstreamModel; cancellationToken.ThrowIfCancellationRequested();
        var result = await ((ICohereChatAdapter)call.Provider).CompleteAsync(payload, call.Context, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); return result;
    }
    /// <inheritdoc />
    public async IAsyncEnumerable<CohereChatEvent> StreamAsync(CohereChatRequest request, LlmCallOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var payload = Snapshot(request, true);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.CohereChat, options, null, cancellationToken, request: payload).ConfigureAwait(false);
        payload["model"] = call.Context.Target.UpstreamModel; cancellationToken.ThrowIfCancellationRequested();
        await foreach (var item in ((ICohereChatAdapter)call.Provider).StreamAsync(payload, call.Context, cancellationToken).ConfigureAwait(false))
        { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        cancellationToken.ThrowIfCancellationRequested();
    }
    /// <summary>首次缓存等待前复制完整原生参数，方法独占模型与流式控制；不填充或改写 message。</summary>
    private static JsonObject Snapshot(CohereChatRequest request, bool stream)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.Parameters.Any(p => p.Key.Equals("model", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("stream", StringComparison.OrdinalIgnoreCase)
            || p.Value.ValueKind == JsonValueKind.Undefined)) throw new ArgumentException("Cohere v1 Chat 扩展参数不能覆盖 model/stream 或包含 Undefined。");
        LlmJsonInput.Request(request.Model, null, request.Parameters);
        return LlmInvocation.Payload(request, request.Model, stream);
    }
}
