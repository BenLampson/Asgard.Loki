using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>内存 WebSocket 边界替身，提供可控分片、关闭与 IO 暂停，不打开端口。</summary>
internal sealed class AzureRealtimeSocket : WebSocket
{
    private readonly Channel<(byte[] Bytes, WebSocketMessageType Type, bool End, WebSocketCloseStatus? Status)> _frames = Channel.CreateUnbounded<(byte[], WebSocketMessageType, bool, WebSocketCloseStatus?)>();
    private readonly CancellationTokenSource _aborted = new();
    private (byte[] Bytes, WebSocketMessageType Type, bool End, WebSocketCloseStatus? Status)? _current;
    private int _offset;
    private WebSocketState _state = WebSocketState.Open;
    private WebSocketCloseStatus? _closeStatus;
    /// <summary>实际写入内容和原生帧标记。</summary>
    internal ConcurrentQueue<(byte[] Bytes, WebSocketMessageType Type, bool End)> Sent { get; } = new();
    /// <summary>发送进入传输边界后的可控等待。</summary>
    internal Func<CancellationToken, Task>? BeforeSend { get; set; }
    /// <summary>模拟传输读取错误。</summary>
    internal Exception? ReceiveError { get; set; }
    internal Action? AfterReceive { get; set; }
    internal Action? AfterSend { get; set; }
    internal Exception? CloseError { get; set; }
    /// <summary>原生关闭帧次数。</summary>
    internal int CloseOutputs { get; private set; }
    /// <summary>资源释放次数。</summary>
    internal int Disposals { get; private set; }
    /// <summary>进入原生发送的次数。</summary>
    internal int SendAttempts { get; private set; }
    /// <inheritdoc />
    public override WebSocketCloseStatus? CloseStatus => _closeStatus;
    /// <inheritdoc />
    public override string? CloseStatusDescription => null;
    /// <inheritdoc />
    public override WebSocketState State => _state;
    /// <inheritdoc />
    public override string? SubProtocol => null;
    /// <summary>注入文本消息，可按字节分片以覆盖 UTF-8 跨帧。</summary>
    internal void Text(string json, int fragmentBytes = int.MaxValue)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        for (var offset = 0; offset < bytes.Length;)
        {
            var count = Math.Min(fragmentBytes, bytes.Length - offset);
            Frame(bytes.AsSpan(offset, count).ToArray(), WebSocketMessageType.Text, offset + count == bytes.Length); offset += count;
        }
    }
    /// <summary>注入任意消息分片。</summary>
    internal void Frame(byte[] bytes, WebSocketMessageType type = WebSocketMessageType.Text, bool end = true)
        => _frames.Writer.TryWrite((bytes, type, end, null));
    /// <summary>注入原生关闭，不通过本地读取完成模拟成功。</summary>
    internal void PeerClose(WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure)
        => _frames.Writer.TryWrite(([], WebSocketMessageType.Close, true, status));
    /// <inheritdoc />
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (ReceiveError is not null) throw ReceiveError;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _aborted.Token);
        if (_current is null) { _current = await _frames.Reader.ReadAsync(operation.Token); _offset = 0; }
        var frame = _current.Value;
        if (frame.Type == WebSocketMessageType.Close)
        {
            _closeStatus = frame.Status; _state = WebSocketState.CloseReceived; _current = null;
            AfterReceive?.Invoke(); return new(0, frame.Type, true, frame.Status, null);
        }
        var count = Math.Min(buffer.Count, frame.Bytes.Length - _offset);
        frame.Bytes.AsSpan(_offset, count).CopyTo(buffer.AsSpan()); _offset += count;
        var consumed = _offset == frame.Bytes.Length; if (consumed) _current = null;
        AfterReceive?.Invoke(); return new(count, frame.Type, consumed && frame.End);
    }
    /// <inheritdoc />
    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        SendAttempts++;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _aborted.Token);
        if (BeforeSend is not null) await BeforeSend(operation.Token);
        operation.Token.ThrowIfCancellationRequested(); Sent.Enqueue((buffer.ToArray(), messageType, endOfMessage)); AfterSend?.Invoke();
    }
    /// <inheritdoc />
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); if (CloseError is not null) throw CloseError; CloseOutputs++; _state = WebSocketState.CloseSent; return Task.CompletedTask;
    }
    /// <inheritdoc />
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
    /// <inheritdoc />
    public override void Abort() { _state = WebSocketState.Aborted; _aborted.Cancel(); }
    /// <inheritdoc />
    public override void Dispose() { Disposals++; _state = WebSocketState.Closed; _aborted.Cancel(); }
}
