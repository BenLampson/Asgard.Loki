using System.Net.WebSockets;

namespace Asgard.Loki.LLM.OpenAI.Internal;

/// <summary>通过标准 ClientWebSocket 建立单次连接；握手失败时释放临时资源。</summary>
internal sealed class DefaultResponsesWebSocketFactory : IOpenAiResponsesWebSocketFactory, IOpenAiRealtimeWebSocketFactory
{
    /// <inheritdoc />
    public async Task<WebSocket> ConnectAsync(Uri address, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(headers);
        cancellationToken.ThrowIfCancellationRequested();
        var socket = new ClientWebSocket();
        try
        {
            foreach (var header in headers) socket.Options.SetRequestHeader(header.Key, header.Value);
            await socket.ConnectAsync(address, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
