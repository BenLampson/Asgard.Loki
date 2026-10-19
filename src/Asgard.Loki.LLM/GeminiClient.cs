using Asgard.Loki.LLM.Internal;

namespace Asgard.Loki.LLM;

/// <summary>将 Gemini 原生调用固定到完整 Session 目标。</summary>
internal sealed class GeminiClient(LlmInvocation invocation) : IGeminiClient
{
    /// <inheritdoc />
    public async Task<GeminiContent> GenerateAsync(GeminiContentRequest request, LlmCallOptions options, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var payload = LlmInvocation.Payload(request, request.Model, false);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.Gemini, options, null, cancellationToken).ConfigureAwait(false);
        payload["model"] = call.Context.Target.UpstreamModel;
        return await ((IGeminiAdapter)call.Provider).GenerateAsync(payload, call.Context, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<GeminiContentEvent> StreamAsync(GeminiContentRequest request, LlmCallOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Validate(request);
        var payload = LlmInvocation.Payload(request, request.Model, true);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.Gemini, options, null, cancellationToken).ConfigureAwait(false);
        payload["model"] = call.Context.Target.UpstreamModel;
        await foreach (var item in ((IGeminiAdapter)call.Provider).StreamAsync(payload, call.Context, cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    /// <summary>拒绝公共字段冲突，保留所有原生扩展参数。</summary>
    private static void Validate(GeminiContentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Contents);
        LlmInvocation.ValidateParameters(request.Parameters, "model", "contents");
        LlmJsonInput.Request(request.Model, request.Contents, request.Parameters);
    }
}
