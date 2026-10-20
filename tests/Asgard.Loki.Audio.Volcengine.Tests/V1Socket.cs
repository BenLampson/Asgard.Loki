using System.Net.WebSockets;
using Xunit;

namespace Asgard.Loki.Audio.Volcengine.Tests;

/// <summary>替代整个 WebSocket 边界，注入碎片、关闭、取消与释放故障。</summary>
internal sealed class V1Socket : WebSocket, IVolcengineV1SpeechWebSocketFactory
{
    private readonly Queue<byte[]?> _messages = new();
    private int _offset;
    private WebSocketState _state = WebSocketState.Open;
    public V1Socket(params string[] frames) { foreach (var name in frames) _messages.Enqueue(V1Fixture.Frame(name)); }
    internal void Enqueue(byte[] bytes) => _messages.Enqueue(bytes);
    internal void EnqueueClose() => _messages.Enqueue(null);
    internal int FragmentSize { get; init; } = int.MaxValue;
    internal bool AutoClose { get; init; } = true;
    internal bool Text { get; init; }
    internal bool FailDispose { get; init; }
    internal bool FailSend { get; init; }
    internal bool FailClose { get; init; }
    internal Action? OnDispose { get; init; }
    internal Action? OnConnect { get; init; }
    internal Action? OnReceive { get; init; }
    internal int Calls { get; private set; }
    internal int Closes { get; private set; }
    internal int Disposals { get; private set; }
    internal byte[] Sent { get; private set; } = [];
    internal WebSocketCloseStatus PeerStatus { get; init; } = WebSocketCloseStatus.NormalClosure;
    public override WebSocketCloseStatus? CloseStatus => _state == WebSocketState.Closed ? PeerStatus : null;
    public override string? CloseStatusDescription => "";
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;
    public Task<WebSocket> ConnectAsync(Uri address, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls++;
        Assert.Equal("wss://openspeech.bytedance.com/api/v1/tts/ws_binary", address.AbsoluteUri);
        Assert.Equal("Bearer;fixture-header-access", headers["Authorization"]); OnConnect?.Invoke();
        return Task.FromResult<WebSocket>(this);
    }
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Assert.Equal(WebSocketMessageType.Binary, messageType); Assert.True(endOfMessage);
        if (FailSend) throw new IOException("send"); Sent = buffer.ToArray(); return Task.CompletedTask;
    }
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); OnReceive?.Invoke();
        if (_messages.Count == 0) { await Task.Delay(Timeout.Infinite, cancellationToken); throw new InvalidOperationException(); }
        var message = _messages.Peek();
        if (message is null)
        {
            _messages.Dequeue(); _state = WebSocketState.Closed;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, PeerStatus, "");
        }
        var count = Math.Min(Math.Min(buffer.Count, FragmentSize), message.Length - _offset);
        message.AsSpan(_offset, count).CopyTo(buffer.AsSpan()); _offset += count;
        var end = _offset == message.Length;
        if (end) { _messages.Dequeue(); _offset = 0; }
        return new WebSocketReceiveResult(count, Text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, end);
    }
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Closes++;
        if (FailClose) throw new IOException("close");
        _state = WebSocketState.CloseSent; if (AutoClose) _messages.Enqueue(null); return Task.CompletedTask;
    }
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotSupportedException();
    public override void Abort() => _state = WebSocketState.Aborted;
    public override void Dispose()
    {
        Disposals++; OnDispose?.Invoke(); if (FailDispose) throw new IOException("dispose"); _state = WebSocketState.Closed;
    }
}
