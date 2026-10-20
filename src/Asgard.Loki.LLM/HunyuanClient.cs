using Asgard.Loki.LLM.Internal;

namespace Asgard.Loki.LLM;

/// <summary>混元原生入口只复用完整目标绑定，不复用其他协议的正文或流式生命周期。</summary>
internal sealed class HunyuanClient(LlmInvocation invocation) : IHunyuanClient
{
    /// <inheritdoc />
    public async Task<HunyuanCompletion> CompleteAsync(HunyuanRequest request, LlmCallOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var payload = Snapshot(request, false);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.Hunyuan, options, null, cancellationToken).ConfigureAwait(false);
        payload["Model"] = call.Context.Target.UpstreamModel; cancellationToken.ThrowIfCancellationRequested();
        var result = await ((IHunyuanAdapter)call.Provider).CompleteAsync(payload, call.Context, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); return result;
    }
    /// <inheritdoc />
    public async IAsyncEnumerable<HunyuanEvent> StreamAsync(HunyuanRequest request, LlmCallOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var payload = Snapshot(request, true);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.Hunyuan, options, null, cancellationToken).ConfigureAwait(false);
        payload["Model"] = call.Context.Target.UpstreamModel; cancellationToken.ThrowIfCancellationRequested();
        await foreach (var item in ((IHunyuanAdapter)call.Provider).StreamAsync(payload, call.Context, cancellationToken).ConfigureAwait(false))
        { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        cancellationToken.ThrowIfCancellationRequested();
    }
    /// <summary>首次异步缓存等待前冻结原生结构；方法独占 PascalCase Model/Stream，不注入小写别名。</summary>
    private static JsonObject Snapshot(HunyuanRequest request, bool stream)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.Parameters.Any(p => p.Key.Equals("Model", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("Stream", StringComparison.OrdinalIgnoreCase)
            || p.Value.ValueKind == JsonValueKind.Undefined)) throw new ArgumentException("混元扩展参数不能覆盖 Model/Stream 或包含 Undefined。");
        LlmJsonInput.Request(request.Model, null, request.Parameters);
        var payload = JsonSerializer.SerializeToNode(request)!.AsObject(); payload["Stream"] = stream; return payload;
    }
}
