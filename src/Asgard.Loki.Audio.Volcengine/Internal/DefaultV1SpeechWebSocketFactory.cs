using System.Net.WebSockets;

namespace Asgard.Loki.Audio.Volcengine.Internal;

/// <summary>拥有单次标准连接；握手失败时释放临时连接。</summary>
internal sealed class DefaultV1SpeechWebSocketFactory : IVolcengineV1SpeechWebSocketFactory
{
    /// <inheritdoc />
    public async Task<WebSocket> ConnectAsync(Uri address, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var socket = new ClientWebSocket();
        try
        {
            foreach (var header in headers) socket.Options.SetRequestHeader(header.Key, header.Value);
            await socket.ConnectAsync(address, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }
}
