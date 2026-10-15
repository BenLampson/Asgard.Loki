using Asgard.Loki.LLM.Internal;

namespace Asgard.Loki.LLM;

/// <summary>处理 Responses 响应链及响应归属。</summary>
internal sealed class ResponsesClient(LlmInvocation invocation) : IResponsesClient
{
    /// <inheritdoc />
    public async Task<Response> CreateAsync(ResponseRequest request, LlmCallOptions options, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var payload = LlmInvocation.Payload(request, request.Model, false);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.Responses, options,
            request.PreviousResponseId, cancellationToken).ConfigureAwait(false);
        payload["model"] = call.Context.Target.UpstreamModel;
        var response = await ((IResponsesAdapter)call.Provider).CreateAsync(
            payload, call.Context, cancellationToken).ConfigureAwait(false);
        if (response.Id is not null) await invocation.Sessions.BindResponseAsync(call.Binding, response.Id, cancellationToken).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ResponseEvent> StreamAsync(ResponseRequest request, LlmCallOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Validate(request);
        var payload = LlmInvocation.Payload(request, request.Model, true);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.Responses, options,
            request.PreviousResponseId, cancellationToken).ConfigureAwait(false);
        payload["model"] = call.Context.Target.UpstreamModel;
        await foreach (var item in ((IResponsesAdapter)call.Provider).StreamAsync(
            payload, call.Context, cancellationToken).ConfigureAwait(false))
        {
            if (item.Data.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object
                && response.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                await invocation.Sessions.BindResponseAsync(call.Binding, id.GetString()!, cancellationToken).ConfigureAwait(false);
            yield return item;
        }
    }

    /// <summary>拒绝冲突字段和无效响应标识。</summary>
    private static void Validate(ResponseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PreviousResponseId is not null) ArgumentException.ThrowIfNullOrWhiteSpace(request.PreviousResponseId);
        LlmInvocation.ValidateParameters(request.Parameters, "model", "input", "previous_response_id");
    }
}
