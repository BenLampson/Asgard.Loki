using Asgard.Loki.LLM.Internal;

namespace Asgard.Loki.LLM;

/// <summary>先快照原生请求，再恢复/建立既有完整目标绑定；不管理会话文本或内容策略。</summary>
internal sealed class ModerationsClient(LlmInvocation invocation) : IModerationsClient
{
    /// <inheritdoc />
    public async Task<Moderation> CreateAsync(ModerationRequest request, LlmCallOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.Parameters.Any(p => p.Key.Equals("model", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("stream", StringComparison.OrdinalIgnoreCase)
            || p.Value.ValueKind == JsonValueKind.Undefined)) throw new ArgumentException("Moderations 扩展参数不能覆盖 model、启用 stream 或包含 Undefined。");
        LlmJsonInput.Request(request.Model, null, request.Parameters);
        var payload = JsonSerializer.SerializeToNode(request)!.AsObject();
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.Moderations, options, null, cancellationToken).ConfigureAwait(false);
        payload["model"] = call.Context.Target.UpstreamModel;
        return await ((IModerationsAdapter)call.Provider).CreateAsync(payload, call.Context, cancellationToken).ConfigureAwait(false);
    }
}
