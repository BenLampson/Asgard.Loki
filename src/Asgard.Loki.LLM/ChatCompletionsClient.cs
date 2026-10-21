using Asgard.Loki.LLM.Internal;

namespace Asgard.Loki.LLM;

/// <summary>将 Chat Completions 调用固定到 Session 所属目标。</summary>
internal sealed class ChatCompletionsClient(LlmInvocation invocation) : IChatCompletionsClient
{
    /// <inheritdoc />
    public async Task<ChatCompletion> CompleteAsync(ChatCompletionRequest request, LlmCallOptions options, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var payload = LlmInvocation.Payload(request, request.Model, false);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.ChatCompletions, options, null, cancellationToken).ConfigureAwait(false);
        payload["model"] = call.Context.Target.UpstreamModel;
        return await ((IChatCompletionsAdapter)call.Provider).CompleteAsync(
            payload, call.Context, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(ChatCompletionRequest request, LlmCallOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Validate(request);
        var payload = LlmInvocation.Payload(request, request.Model, true);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.ChatCompletions, options, null, cancellationToken).ConfigureAwait(false);
        payload["model"] = call.Context.Target.UpstreamModel;
        await foreach (var item in ((IChatCompletionsAdapter)call.Provider).StreamAsync(
            payload, call.Context, cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    /// <summary>校验公共字段，不删减供应商扩展内容。</summary>
    private static void Validate(ChatCompletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Messages);
        LlmInvocation.ValidateParameters(request.Parameters, "model", "messages");
        LlmJsonInput.Request(request.Model, request.Messages, request.Parameters);
    }
}
