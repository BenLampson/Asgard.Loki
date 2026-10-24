using Asgard.Loki.LLM.Internal;

namespace Asgard.Loki.LLM;

/// <summary>原生 JSON 在 Session 等待前复制；不插入 stream 或供应商版本字段。</summary>
internal sealed class BedrockConverseClient(LlmInvocation invocation) : IBedrockConverseClient
{
    /// <inheritdoc />
    public async Task<BedrockConverseResponse> ConverseAsync(BedrockConverseRequest request, LlmCallOptions options, CancellationToken cancellationToken = default)
    {
        var payload = Payload(request); var call = await invocation.ResolveAsync(request.Model, LlmProtocol.BedrockConverse, options, null, cancellationToken, request: payload).ConfigureAwait(false); payload["model"] = call.Context.Target.UpstreamModel;
        var result = await ((IBedrockConverseAdapter)call.Provider).ConverseAsync(payload, call.Context, cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested(); return result;
    }
    /// <inheritdoc />
    public async IAsyncEnumerable<BedrockConverseEvent> StreamAsync(BedrockConverseRequest request, LlmCallOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = Payload(request); var call = await invocation.ResolveAsync(request.Model, LlmProtocol.BedrockConverse, options, null, cancellationToken, request: payload).ConfigureAwait(false); payload["model"] = call.Context.Target.UpstreamModel;
        await foreach (var item in ((IBedrockConverseAdapter)call.Provider).StreamAsync(payload, call.Context, cancellationToken).ConfigureAwait(false)) { cancellationToken.ThrowIfCancellationRequested(); yield return item; } cancellationToken.ThrowIfCancellationRequested();
    }
    /// <summary>只声明 model 为 SDK 路由字段，其余缺省/null/未知正文完全保留。</summary>
    private static JsonObject Payload(BedrockConverseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); LlmInvocation.ValidateParameters(request.Parameters, "model"); LlmJsonInput.Request(request.Model, null, request.Parameters); return JsonSerializer.SerializeToNode(request)!.AsObject();
    }
}
