using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.OpenAI.Internal;

/// <summary>原生 Responses 事件连接；状态只用于关联与终态验证，不执行任何应用流程。</summary>
internal sealed class OpenAiResponsesWebSocketConnection(ResponsesWebSocketTransport transport, LlmResultMetadata metadata,
    OpenAiResponsesWebSocketOptions options) : IResponsesWebSocketConnection
{
    private readonly ResponsesWebSocketState _state = new(options);
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private int _reader;
    private int _closed;
    private int _disposed;
    /// <inheritdoc />
    public LlmResultMetadata Metadata { get; } = metadata;

    /// <inheritdoc />
    public ValueTask SendAsync(JsonObject message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message); CheckOpen(); cancellationToken.ThrowIfCancellationRequested();
        var snapshot = message.DeepClone().AsObject();
        if (snapshot["type"]?.GetValue<string>() == "response.create")
        {
            if (snapshot["model"]?.GetValue<string>() != Metadata.UpstreamModel) throw new ArgumentException("原生连接不能改变绑定的实际模型。", nameof(message));
            if (snapshot.ContainsKey("stream") || snapshot.ContainsKey("background")) throw new ArgumentException("WebSocket 不接受 stream/background 字段。", nameof(message));
        }
        return SendSnapshotAsync(snapshot, cancellationToken);
    }

    /// <summary>在实际写入前登记关联；本地失败仅回滚未被消费的登记。</summary>
    private async ValueTask SendSnapshotAsync(JsonObject message, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckOpen(); ResponsesWebSocketSubmission? request;
            lock (_stateLock) request = _state.Register(message);
            try { await transport.SendAsync(message, cancellationToken).ConfigureAwait(false); }
            catch { lock (_stateLock) _state.Rollback(request); throw; }
        }
        finally { _writer.Release(); }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ResponseEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CheckOpen();
        if (Interlocked.CompareExchange(ref _reader, 1, 0) != 0) throw new InvalidOperationException("原生连接只能有一个并发读取者。");
        try
        {
            while (true)
            {
                var data = await ReadCheckedAsync(cancellationToken).ConfigureAwait(false);
                if (data is null) yield break;
                yield return new ResponseEvent(data.Value, Metadata);
                bool fatal; lock (_stateLock) fatal = _state.Fatal;
                if (fatal) { await DisposeAsync().ConfigureAwait(false); yield break; }
            }
        }
        finally { Volatile.Write(ref _reader, 0); }
    }

    /// <summary>损坏的消息或状态不允许继续复用连接；从不生成虚假的完成事件。</summary>
    private async ValueTask<JsonElement?> ReadCheckedAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Volatile.Read(ref _closed) != 0) { lock (_stateLock) _state.Complete(); return null; }
            var data = await transport.ReadAsync(cancellationToken).ConfigureAwait(false);
            lock (_stateLock)
            {
                if (data is null) { Interlocked.Exchange(ref _closed, 1); _state.Complete(); }
                else _state.Accept(data.Value);
            }
            return data;
        }
        catch { await DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <inheritdoc />
    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) await transport.CloseAsync(cancellationToken).ConfigureAwait(false);
    }
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        if (Interlocked.Exchange(ref _disposed, 1) == 0) await transport.DisposeAsync().ConfigureAwait(false);
    }
    /// <summary>显式关闭、传输错误或连接级错误以后，调用者必须显式建立新连接。</summary>
    private void CheckOpen()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _closed) != 0) throw new InvalidOperationException("原生连接已关闭。");
        lock (_stateLock) if (_state.Fatal) throw new InvalidOperationException("原生连接已经失败。");
    }
}
