using System.Net.WebSockets;

namespace Asgard.Loki.LLM.Xunfei.Tests;

/// <summary>离线一次握手工厂，捕获实际签名 URI，绝不建立网络连接。</summary>
internal sealed class SparkSocketFactory : IXunfeiSparkWebSocketFactory, IDisposable
{
    public string BindingIdentity { get; set; } = "fixture-spark-sockets";
    internal int Calls { get; private set; }
    internal Uri? Address { get; private set; }
    internal bool Disposed { get; private set; }
    internal SparkSocket Socket { get; set; } = SparkSocket.Native();
    internal Func<CancellationToken, Task<WebSocket>>? Connect { get; set; }
    public Task<WebSocket> ConnectAsync(Uri address, CancellationToken cancellationToken = default)
    { Calls++; Address = address; cancellationToken.ThrowIfCancellationRequested(); return Connect is null ? Task.FromResult<WebSocket>(Socket) : Connect(cancellationToken); }
    public void Dispose() => Disposed = true;
}
