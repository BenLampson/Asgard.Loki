using Asgard.Loki.LLM.Internal;

namespace Asgard.Loki.LLM;

/// <summary>原生 Workers AI 文本 只复用完整目标绑定，协议正文和流式状态仍归供应商所有。</summary>
internal sealed class WorkersAiTextClient(LlmInvocation invocation) : IWorkersAiTextClient
{
    /// <inheritdoc />
    public async Task<WorkersAiTextCompletion> CompleteAsync(WorkersAiTextRequest request, LlmCallOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var payload = Snapshot(request, false);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.WorkersAiText, options, null, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await ((IWorkersAiTextAdapter)call.Provider).CompleteAsync(payload, call.Context, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); return result;
    }
    /// <inheritdoc />
    public async IAsyncEnumerable<WorkersAiTextEvent> StreamAsync(WorkersAiTextRequest request, LlmCallOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var payload = Snapshot(request, true);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.WorkersAiText, options, null, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await foreach (var item in ((IWorkersAiTextAdapter)call.Provider).StreamAsync(payload, call.Context, cancellationToken).ConfigureAwait(false))
        { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        cancellationToken.ThrowIfCancellationRequested();
    }
    /// <summary>首次缓存等待前复制完整原生参数，原生 URL 独占模型，方法独占流式控制；不注入正文 model 或生成默认值。</summary>
    private static JsonObject Snapshot(WorkersAiTextRequest request, bool stream)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.Parameters.Any(p => p.Key.Equals("model", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("stream", StringComparison.OrdinalIgnoreCase)
            || p.Value.ValueKind == JsonValueKind.Undefined)) throw new ArgumentException("Workers AI 文本 扩展参数不能覆盖 model/stream 或包含 Undefined。");
        LlmJsonInput.Request(request.Model, null, request.Parameters);
        var payload = JsonSerializer.SerializeToNode(request.Parameters)!.AsObject(); payload["stream"] = stream; return payload;
    }
}
