using Asgard.Loki.LLM.Internal;

namespace Asgard.Loki.LLM;

/// <summary>Ollama 原生 Chat 独立于 OpenAI Chat，复用完整目标和显式首次绑定协调。</summary>
internal sealed class OllamaChatClient(LlmInvocation invocation) : IOllamaChatClient
{
    /// <inheritdoc />
    public async Task<OllamaChatResponse> CompleteAsync(OllamaChatRequest request, LlmCallOptions options, CancellationToken cancellationToken = default)
    {
        Validate(request); var payload = LlmInvocation.Payload(request, request.Model, false);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.OllamaChat, options, null, cancellationToken).ConfigureAwait(false);
        payload["model"] = call.Context.Target.UpstreamModel;
        return await ((IOllamaChatAdapter)call.Provider).CompleteAsync(payload, call.Context, cancellationToken).ConfigureAwait(false);
    }
    /// <inheritdoc />
    public async IAsyncEnumerable<OllamaChatEvent> StreamAsync(OllamaChatRequest request, LlmCallOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Validate(request); var payload = LlmInvocation.Payload(request, request.Model, true);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.OllamaChat, options, null, cancellationToken).ConfigureAwait(false);
        payload["model"] = call.Context.Target.UpstreamModel;
        await foreach (var item in ((IOllamaChatAdapter)call.Provider).StreamAsync(payload, call.Context, cancellationToken).ConfigureAwait(false))
        { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        cancellationToken.ThrowIfCancellationRequested();
    }
    /// <summary>复制/序列化之前拒绝无效 Unicode，避免原生正文先变成替代字符。</summary>
    private static void Validate(OllamaChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Messages);
        LlmInvocation.ValidateParameters(request.Parameters, "model", "messages");
        LlmJsonInput.Request(request.Model, request.Messages, request.Parameters);
    }
}
