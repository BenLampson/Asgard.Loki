using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Azure.Internal;

/// <summary>Azure 私有的有界 Realtime JSON 分帧，认证守卫贯穿排队、实际 IO 和结果交付。</summary>
internal sealed class AzureRealtimeTransport : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly Action _validate;
    private int _socketDisposed;
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
    internal AzureRealtimeTransport(WebSocket socket, int maximumMessageBytes, Action validate)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumMessageBytes, 0);
        if (socket.State != WebSocketState.Open) throw new ArgumentException("工厂必须返回已经打开的 WebSocket。", nameof(socket));
        _validate = validate;
        _socket = socket; _maximumMessageBytes = maximumMessageBytes; _stopped = _lifetime.Token;
    }

    /// <summary>同步复制正文再排队；只串行实际写入，不禁止服务端原生多流并行。</summary>
    internal ValueTask SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfUnavailable(); cancellationToken.ThrowIfCancellationRequested(); Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message.DeepClone());
        if (bytes.Length > _maximumMessageBytes) throw new ArgumentException("WebSocket JSON 消息超过显式大小限制。", nameof(message));
        _ = AzureJson.Parse(bytes);
        return SendBytesAsync(bytes, cancellationToken);
    }

    /// <summary>任何已经进入 socket 的失败或取消均终止连接，不自动重发。</summary>
    private async ValueTask SendBytesAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopped);
        await _writer.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable(); operation.Token.ThrowIfCancellationRequested(); Validate();
            try { await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, operation.Token).ConfigureAwait(false); operation.Token.ThrowIfCancellationRequested(); Validate(); }
            catch { Fail(); throw; }
        }
        finally { _writer.Release(); }
    }

    /// <summary>合并 UTF-8 文本分片后解析；正常关闭返回 null，其他传输终止明确失败。</summary>
    internal async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnavailable(); cancellationToken.ThrowIfCancellationRequested(); Validate();
        if (Interlocked.CompareExchange(ref _reader, 1, 0) != 0) throw new InvalidOperationException("WebSocket 传输只能有一个读取者。");
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopped);
            using var message = new MemoryStream();
            while (true)
            {
                Validate();
                var frame = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested(); Validate();
                if (frame.MessageType == WebSocketMessageType.Close)
                {
                    if (message.Length != 0) throw new InvalidDataException("WebSocket 在 JSON 消息分片结束之前关闭。");
                    if (_socket.CloseStatus != WebSocketCloseStatus.NormalClosure) throw new WebSocketException("上游 WebSocket 非正常关闭。");
                    await CloseAsync(cancellationToken).ConfigureAwait(false);
                    return null;
                }
                if (frame.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("原生 WebSocket 只接受文本 JSON 消息。");
                if (message.Length + frame.Count > _maximumMessageBytes) throw new InvalidDataException("上游 WebSocket 消息超过显式大小限制。");
                message.Write(buffer, 0, frame.Count);
                if (!frame.EndOfMessage) continue;
                JsonElement data;
                try { data = AzureJson.Parse(message.ToArray()); }
                catch (DecoderFallbackException error) { throw new InvalidDataException("Azure Realtime 文本不是有效 UTF-8。", error); }
                if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Azure Realtime 消息必须是 JSON 对象。");
                operation.Token.ThrowIfCancellationRequested(); Validate(); return data;
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
        catch { Fail(); throw; }
        finally { Stop(); }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Interlocked.Exchange(ref _closing, 1); Stop();
            lock (_stopLock) { try { ReleaseSocket(); } finally { _lifetime.Dispose(); _lifetimeDisposed = true; } }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>停止任何不确定的传输，不把失败当成可安全重放的请求。</summary>
    private void Fail() { Interlocked.Exchange(ref _failed, 1); Stop(); ReleaseSocket(); }
    /// <summary>身份变化立即终止并关闭拥有的传输，不等待调用者再次释放。</summary>
    private void Validate() { try { _validate(); } catch { Fail(); throw; } }
    /// <summary>底层 socket 的所有退出路径只释放一次。</summary>
    private void ReleaseSocket() { if (Interlocked.Exchange(ref _socketDisposed, 1) == 0) _socket.Dispose(); }
    /// <summary>中断正在等待的 IO，资源最终由调用者 DisposeAsync 释放。</summary>
    private void Stop()
    {
        lock (_stopLock)
        {
            if (_lifetimeDisposed) return;
            _lifetime.Cancel(); if (Volatile.Read(ref _socketDisposed) == 0) _socket.Abort();
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
