using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>只记录握手并返回内存 socket；供应商与客户端不能释放借用的工厂。</summary>
internal sealed class StubResponsesWebSocketFactory : IOpenAiResponsesWebSocketFactory, IDisposable
{
    /// <summary>可按测试变更稳定身份。</summary>
    internal string Version { get; set; } = "1";
    /// <inheritdoc />
    public string BindingIdentity => "in-memory-ws:" + Version;
    /// <summary>完整握手地址及只属于本次调用的头。</summary>
    internal ConcurrentQueue<(Uri Address, IReadOnlyDictionary<string, string> Headers)> Calls { get; } = new();
    /// <summary>显式返回的内存连接。</summary>
    internal ConcurrentQueue<StubWebSocket> Sockets { get; } = new();
    /// <summary>创建、失败或暂停握手。</summary>
    internal Func<CancellationToken, Task<WebSocket>>? Connect { get; set; }
    /// <summary>借用工厂不应被释放。</summary>
    internal int Disposals { get; private set; }
    /// <inheritdoc />
    public async Task<WebSocket> ConnectAsync(Uri address, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls.Enqueue((address, new Dictionary<string, string>(headers)));
        if (Connect is not null) return await Connect(cancellationToken);
        var socket = new StubWebSocket(); Sockets.Enqueue(socket); return socket;
    }
    /// <inheritdoc />
    public void Dispose() => Disposals++;
}
