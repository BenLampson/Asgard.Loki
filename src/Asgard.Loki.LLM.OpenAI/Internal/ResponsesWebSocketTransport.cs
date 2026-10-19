using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.OpenAI.Internal;

/// <summary>仅管理供应商私有的有界 JSON 分帧；生成生命周期由上层原生协议状态处理。</summary>
internal sealed class ResponsesWebSocketTransport : IAsyncDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly WebSocket _socket;
    private readonly int _maximumMessageBytes;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _stopped;
    private readonly object _stopLock = new();
    private bool _lifetimeDisposed;
    private int _reader;
    private int _closing;
    private int _failed;
    private int _disposed;

    /// <summary>取得已连接 socket 的所有权；大小限制同时适用于收发完整消息。</summary>
    internal ResponsesWebSocketTransport(WebSocket socket, int maximumMessageBytes)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumMessageBytes, 0);
        if (socket.State != WebSocketState.Open) throw new ArgumentException("工厂必须返回已经打开的 WebSocket。", nameof(socket));
        _socket = socket; _maximumMessageBytes = maximumMessageBytes; _stopped = _lifetime.Token;
    }

    /// <summary>同步复制正文再排队；只串行实际写入，不禁止服务端原生多流并行。</summary>
    internal ValueTask SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfUnavailable(); cancellationToken.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message.DeepClone());
        if (bytes.Length > _maximumMessageBytes) throw new ArgumentException("WebSocket JSON 消息超过显式大小限制。", nameof(message));
        return SendBytesAsync(bytes, cancellationToken);
    }

    /// <summary>任何已经进入 socket 的失败或取消均终止连接，不自动重发。</summary>
    private async ValueTask SendBytesAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopped);
        await _writer.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable(); operation.Token.ThrowIfCancellationRequested();
            try { await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, operation.Token).ConfigureAwait(false); }
            catch { Fail(); throw; }
        }
        finally { _writer.Release(); }
    }

    /// <summary>合并 UTF-8 文本分片后解析；正常关闭返回 null，其他传输终止明确失败。</summary>
    internal async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnavailable(); cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _reader, 1, 0) != 0) throw new InvalidOperationException("WebSocket 传输只能有一个读取者。");
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopped);
            using var message = new MemoryStream();
            while (true)
            {
                var frame = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), operation.Token).ConfigureAwait(false);
                if (frame.MessageType == WebSocketMessageType.Close)
                {
                    if (message.Length != 0) throw new InvalidDataException("WebSocket 在 JSON 消息分片结束之前关闭。");
                    if (_socket.CloseStatus != WebSocketCloseStatus.NormalClosure) throw new WebSocketException("上游 WebSocket 非正常关闭。");
                    await CloseAsync(cancellationToken).ConfigureAwait(false);
                    return null;
                }
                if (frame.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Responses WebSocket 只接受文本 JSON 消息。");
                if (message.Length + frame.Count > _maximumMessageBytes) throw new InvalidDataException("上游 WebSocket 消息超过显式大小限制。");
                message.Write(buffer, 0, frame.Count);
                if (!frame.EndOfMessage) continue;
                try { _ = StrictUtf8.GetCharCount(message.GetBuffer(), 0, checked((int)message.Length)); }
                catch (DecoderFallbackException error) { throw new InvalidDataException("WebSocket 文本不是有效的 UTF-8。", error); }
                using var json = JsonDocument.Parse(message.GetBuffer().AsMemory(0, checked((int)message.Length)));
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("原生 WebSocket 消息必须是 JSON 对象。");
                return json.RootElement.Clone();
            }
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _closing) != 0 && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch { Fail(); throw; }
        finally { ArrayPool<byte>.Shared.Return(buffer); Volatile.Write(ref _reader, 0); }
    }

    /// <summary>发送关闭帧后终止本地读写；不启动隐式接收循环等待远端。</summary>
    internal async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0) return;
        try
        {
            if (!await _writer.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken).ConfigureAwait(false);
            }
            finally { _writer.Release(); }
        }
        finally { Stop(); }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Interlocked.Exchange(ref _closing, 1); Stop();
            lock (_stopLock) { _socket.Dispose(); _lifetime.Dispose(); _lifetimeDisposed = true; }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>停止任何不确定的传输，不把失败当成可安全重放的请求。</summary>
    private void Fail() { Interlocked.Exchange(ref _failed, 1); Stop(); }
    /// <summary>中断正在等待的 IO，资源最终由调用者 DisposeAsync 释放。</summary>
    private void Stop()
    {
        lock (_stopLock)
        {
            if (_lifetimeDisposed) return;
            _lifetime.Cancel(); _socket.Abort();
        }
    }
    /// <summary>所有后续操作都保留明确的关闭、失败与释放状态。</summary>
    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _closing) != 0) throw new InvalidOperationException("WebSocket 连接已经关闭。");
        if (Volatile.Read(ref _failed) != 0) throw new WebSocketException("WebSocket 连接已失败，不允许继续收发或重试。");
    }
}
