using Asgard.Loki.LLM.Internal;

namespace Asgard.Loki.LLM;

/// <summary>处理 Responses 响应链及响应归属。</summary>
internal sealed class ResponsesClient(LlmInvocation invocation) : IResponsesClient
{
    /// <inheritdoc />
    public async Task<IResponsesWebSocketConnection> OpenWebSocketAsync(string model, LlmCallOptions options, CancellationToken cancellationToken = default)
    {
        var call = await invocation.ResolveAsync(model, LlmProtocol.Responses, options, null, cancellationToken,
            LlmTargetFeatures.ResponsesWebSocket).ConfigureAwait(false);
        var connection = await ((IResponsesWebSocketAdapter)call.Provider).OpenWebSocketAsync(call.Context, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("原生适配器未返回连接。");
        try
        {
            await invocation.ValidateBoundAsync(call.Binding, null, cancellationToken).ConfigureAwait(false);
            if (connection.Metadata != call.Context.Metadata) throw new InvalidOperationException("连接返回了不同目标的元数据。");
            return new BoundResponsesWebSocketConnection(invocation, call.Binding, connection, call.Context.Metadata);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ResponseCompaction> CompactAsync(ResponseCompactionRequest request, LlmCallOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PreviousResponseId is not null) ArgumentException.ThrowIfNullOrWhiteSpace(request.PreviousResponseId);
        if (request.Input is not null && request.Input is not JsonArray
            && (request.Input is not JsonValue text || !text.TryGetValue<string>(out _)))
            throw new ArgumentException("压缩输入必须为文本或原生输入项数组。", nameof(request));
        LlmInvocation.ValidateParameters(request.Parameters, "model", "input", "previous_response_id", "stream");
        LlmJsonInput.Request(request.Model, request.Input, request.Parameters, request.PreviousResponseId);
        var payload = JsonSerializer.SerializeToNode(request)!.AsObject();
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.Responses, options,
            request.PreviousResponseId, cancellationToken, LlmTargetFeatures.ResponsesCompaction).ConfigureAwait(false);
        payload["model"] = call.Context.Target.UpstreamModel;
        return await ((IResponsesCompactionAdapter)call.Provider).CompactAsync(payload, call.Context, cancellationToken).ConfigureAwait(false);
    }

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
        LlmJsonInput.Request(request.Model, request.Input, request.Parameters, request.PreviousResponseId);
    }
}
