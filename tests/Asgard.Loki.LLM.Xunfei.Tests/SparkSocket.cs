using System.Net.WebSockets;
using System.Text.Json;

namespace Asgard.Loki.LLM.Xunfei.Tests;

/// <summary>三字节碎片和显式关闭确认替身；禁止用 CloseAsync 隐式吞掉剩余模型消息。</summary>
internal sealed class SparkSocket : WebSocket
{
    private readonly Queue<(byte[] Body, WebSocketMessageType Type, WebSocketCloseStatus? Close)> _queue = new();
    private int _offset;
    private WebSocketState _state = WebSocketState.Open;
    private WebSocketCloseStatus? _close;
    internal int FragmentBytes { get; set; } = 3;
    internal bool EmptyFragments { get; set; }
    internal int? CloseAtRead { get; set; }
    internal int Sends { get; private set; }
    internal int Reads { get; private set; }
    internal int CloseOutputs { get; private set; }
    internal int CloseCalls { get; private set; }
    internal int Disposals { get; private set; }
    internal byte[]? Sent { get; private set; }
    internal WebSocketMessageType? SentType { get; private set; }
    internal bool SentEnd { get; private set; }
    internal Action? OnDispose { get; set; }
    internal Func<ArraySegment<byte>, CancellationToken, Task>? OnSend { get; set; }
    internal Func<CancellationToken, Task>? OnRead { get; set; }
    internal Func<CancellationToken, Task>? OnClose { get; set; }
    public override WebSocketCloseStatus? CloseStatus => _close;
    public override string? CloseStatusDescription => "fixture close";
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;
    internal void Add(byte[] body, WebSocketMessageType type = WebSocketMessageType.Text) => _queue.Enqueue((body, type, null));
    internal void Add(JsonElement data) => Add(System.Text.Encoding.UTF8.GetBytes(data.GetRawText()));
    internal void AddClose(WebSocketCloseStatus? status = WebSocketCloseStatus.NormalClosure) => _queue.Enqueue(([], WebSocketMessageType.Close, status));
    internal static SparkSocket Native(string frames = "frames", bool close = true)
    { var socket = new SparkSocket(); foreach (var item in SparkFixture.Part(frames).EnumerateArray()) socket.Add(item); if (close) socket.AddClose(); return socket; }
    internal static SparkSocket Transcript()
    { var socket = new SparkSocket(); foreach (var line in SparkFixture.Transcript()) socket.Add(System.Text.Encoding.UTF8.GetBytes(line)); socket.AddClose(); return socket; }
    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); Sends++; Sent = buffer.ToArray(); SentType = messageType; SentEnd = endOfMessage; if (OnSend is not null) await OnSend(buffer, cancellationToken); }
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        Reads++; cancellationToken.ThrowIfCancellationRequested(); if (OnRead is not null) await OnRead(cancellationToken); cancellationToken.ThrowIfCancellationRequested();
        if (CloseAtRead is { } closeAt && Reads >= closeAt) return new(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "fixture mid-fragment close");
        if (_queue.Count == 0) throw new IOException("fixture missing close/message"); var item = _queue.Peek();
        if (item.Type == WebSocketMessageType.Close) { _queue.Dequeue(); _close = item.Close; _state = CloseOutputs > 0 ? WebSocketState.Closed : WebSocketState.CloseReceived; return new(0, item.Type, true, item.Close, "fixture close"); }
        if (EmptyFragments) return new(0, WebSocketMessageType.Text, false);
        var count = Math.Min(Math.Min(FragmentBytes, buffer.Count), item.Body.Length - _offset); item.Body.AsSpan(_offset, count).CopyTo(buffer.AsSpan()); _offset += count; var end = _offset == item.Body.Length;
        if (end) { _queue.Dequeue(); _offset = 0; } return new(count, item.Type, end);
    }
    public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); CloseOutputs++; _state = WebSocketState.CloseSent; if (OnClose is not null) await OnClose(cancellationToken); }
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    { CloseCalls++; throw new InvalidOperationException("Cannot consume native data through CloseAsync"); }
    public override void Abort() => _state = WebSocketState.Aborted;
    public override void Dispose() { Disposals++; _state = WebSocketState.Closed; OnDispose?.Invoke(); }
}
