using Asgard.Loki.LLM.Internal;

namespace Asgard.Loki.LLM;

/// <summary>先复制请求再等待 Session；context 和 prompt 不变成 SDK 托管历史。</summary>
internal sealed class OllamaGenerateClient(LlmInvocation invocation) : IOllamaGenerateClient
{
    /// <inheritdoc />
    public async Task<OllamaGenerateResponse> GenerateAsync(OllamaGenerateRequest request, LlmCallOptions options, CancellationToken cancellationToken = default)
    {
        var payload = Payload(request, false);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.OllamaGenerate, options, null, cancellationToken).ConfigureAwait(false);
        payload["model"] = call.Context.Target.UpstreamModel;
        return await ((IOllamaGenerateAdapter)call.Provider).GenerateAsync(payload, call.Context, cancellationToken).ConfigureAwait(false);
    }
    /// <inheritdoc />
    public async IAsyncEnumerable<OllamaGenerateEvent> StreamAsync(OllamaGenerateRequest request, LlmCallOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = Payload(request, true);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.OllamaGenerate, options, null, cancellationToken).ConfigureAwait(false);
        payload["model"] = call.Context.Target.UpstreamModel;
        await foreach (var item in ((IOllamaGenerateAdapter)call.Provider).StreamAsync(payload, call.Context, cancellationToken).ConfigureAwait(false))
        { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        cancellationToken.ThrowIfCancellationRequested();
    }
    /// <summary>模型与扩展 JSON 固定在首次等待前；不添加 prompt、context 或其他缺省字段。</summary>
    private static JsonObject Payload(OllamaGenerateRequest request, bool stream)
    {
        ArgumentNullException.ThrowIfNull(request); LlmInvocation.ValidateParameters(request.Parameters, "model");
        _ = new System.Text.UTF8Encoding(false, true).GetByteCount(request.Model);
        return LlmInvocation.Payload(request, request.Model, stream);
    }
}
