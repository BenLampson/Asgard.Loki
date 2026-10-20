using System.Net.WebSockets;

namespace Asgard.Loki.LLM.Xunfei.Internal;

/// <summary>一次标准 ClientWebSocket 握手；失败时释放连接，不把签名 URL 放进异常文本。</summary>
internal sealed class DefaultSparkWebSocketFactory : IXunfeiSparkWebSocketFactory
{
    /// <inheritdoc />
    public string BindingIdentity => "spark-client-websocket-v1";
    /// <inheritdoc />
    public async Task<WebSocket> ConnectAsync(Uri address, CancellationToken cancellationToken = default)
    {
        var socket = new ClientWebSocket();
        try { await socket.ConnectAsync(address, cancellationToken).ConfigureAwait(false); return socket; }
        catch (OperationCanceledException) { socket.Dispose(); throw new OperationCanceledException("星火 WebSocket 握手已取消。", cancellationToken); }
        catch (WebSocketException error) { socket.Dispose(); throw new WebSocketException(error.WebSocketErrorCode, "星火 WebSocket 握手失败。"); }
        catch { socket.Dispose(); throw new InvalidOperationException("星火 WebSocket 握手失败。"); }
    }
}
