using Asgard.Loki.LLM.Internal;

namespace Asgard.Loki.LLM;

/// <summary>将 ClaudeMessages 原生调用固定到完整 Session 目标。</summary>
internal sealed class ClaudeMessagesClient(LlmInvocation invocation) : IClaudeMessagesClient
{
    /// <inheritdoc />
    public async Task<ClaudeMessage> CreateAsync(ClaudeMessageRequest request, LlmCallOptions options, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var payload = LlmInvocation.Payload(request, request.Model, false);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.ClaudeMessages, options, null, cancellationToken).ConfigureAwait(false);
        payload["model"] = call.Context.Target.UpstreamModel;
        return await ((IClaudeMessagesAdapter)call.Provider).CreateAsync(payload, call.Context, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ClaudeMessageEvent> StreamAsync(ClaudeMessageRequest request, LlmCallOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Validate(request);
        var payload = LlmInvocation.Payload(request, request.Model, true);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.ClaudeMessages, options, null, cancellationToken).ConfigureAwait(false);
        payload["model"] = call.Context.Target.UpstreamModel;
        await foreach (var item in ((IClaudeMessagesAdapter)call.Provider).StreamAsync(payload, call.Context, cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    /// <summary>拒绝公共字段冲突，保留所有原生扩展参数。</summary>
    private static void Validate(ClaudeMessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Messages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.MaxTokens);
        LlmInvocation.ValidateParameters(request.Parameters, "model", "messages", "max_tokens");
    }
}
