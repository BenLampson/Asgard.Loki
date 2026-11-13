namespace Asgard.Loki.LLM.Internal;

/// <summary>约束一个原生双工连接始终使用原 Session、实际模型与响应归属。</summary>
internal sealed class BoundResponsesWebSocketConnection(LlmInvocation invocation, LlmSessionBinding binding,
    IResponsesWebSocketConnection connection, LlmResultMetadata metadata, LlmTarget target) : IResponsesWebSocketConnection
{
    private readonly SemaphoreSlim _send = new(1, 1);
    private int _reader;
    private int _closed;
    private int _disposed;

    /// <inheritdoc />
    public LlmResultMetadata Metadata { get; } = metadata;

    /// <inheritdoc />
    public ValueTask SendAsync(JsonObject message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfClosed();
        // 必须在任何等待之前复制输入，调用者之后的修改不能影响实际发送。
        LlmJsonInput.Node(message);
        var snapshot = message.DeepClone().AsObject();
        var type = RequiredText(snapshot, "type");
        string? previous = null;
        if (type == "response.create")
        {
            if (snapshot.ContainsKey("model") && RequiredText(snapshot, "model") != binding.Model)
                throw new InvalidOperationException("同一连接不能改变对外模型。");
            if (snapshot.ContainsKey("stream") || snapshot.ContainsKey("background"))
                throw new ArgumentException("WebSocket 创建事件不接受 stream 或 background 字段。", nameof(message));
            if (snapshot["previous_response_id"] is not null) previous = RequiredText(snapshot, "previous_response_id");
            LlmOutputFormats.Validate(target, LlmProtocol.Responses, snapshot);
            snapshot["model"] = Metadata.UpstreamModel;
        }
        else if (type == "response.steer") previous = RequiredText(snapshot, "previous_response_id");
        else if (type == "response.cancel" && snapshot["response_id"] is not null)
            previous = RequiredText(snapshot, "response_id");
        return SendSnapshotAsync(snapshot, previous, cancellationToken);
    }

    /// <summary>仅串行化实际写入与归属验证；不限制服务端支持的并行流。</summary>
    private async ValueTask SendSnapshotAsync(JsonObject snapshot, string? previous, CancellationToken cancellationToken)
    {
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            await invocation.ValidateBoundAsync(binding, previous, cancellationToken).ConfigureAwait(false);
            ThrowIfClosed();
            await connection.SendAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally { _send.Release(); }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ResponseEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        if (Interlocked.CompareExchange(ref _reader, 1, 0) != 0) throw new InvalidOperationException("同一连接只允许一个并发读取者。");
        try
        {
            await invocation.ValidateBoundAsync(binding, null, cancellationToken).ConfigureAwait(false);
            ThrowIfClosed();
            await foreach (var item in connection.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (item.Type is "response.created" or "response.in_progress" or "response.completed" or "response.failed" or "response.incomplete")
                {
                    if (!item.Data.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object
                        || !response.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                        throw new InvalidDataException("响应生命周期事件缺少有效的原生响应 ID。");
                    await invocation.ValidateBoundAsync(binding, null, cancellationToken).ConfigureAwait(false);
                    await invocation.Sessions.BindResponseAsync(binding, id.GetString()!, cancellationToken).ConfigureAwait(false);
                }
                // 完整保留事件及扩展字段，公共元数据固定为 SDK 原先选定的完整目标。
                yield return new ResponseEvent(item.Data, Metadata, item.EventName, item.EventId, item.RetryMilliseconds);
            }
        }
        finally { Volatile.Write(ref _reader, 0); }
    }

    /// <inheritdoc />
    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) await connection.CloseAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        if (Interlocked.Exchange(ref _disposed, 1) == 0) await connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>区分原生 JSON 文本与缺失、空白或其他类型，不隐式字符串化。</summary>
    private static string RequiredText(JsonObject message, string name)
        => message[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text : throw new ArgumentException($"{name} 必须为非空文本。", nameof(message));

    /// <summary>关闭或释放后不能重新使用原连接。</summary>
    private void ThrowIfClosed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _closed) != 0) throw new InvalidOperationException("连接已经关闭。");
    }
}
