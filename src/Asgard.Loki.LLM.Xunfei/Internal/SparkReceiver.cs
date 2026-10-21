using System.Net.WebSockets;

namespace Asgard.Loki.LLM.Xunfei.Internal;

/// <summary>按完整 WebSocket 文本消息组装字节，不拼接生成文本；整次消息字节/数量均有界。</summary>
internal sealed class SparkReceiver(XunfeiSparkOptions options)
{
    private long _total;
    private int _events;
    /// <summary>关闭控制帧独立返回，不能把中途关闭当完整 JSON 消息。</summary>
    internal async ValueTask<(byte[]? Body, WebSocketCloseStatus? Close)> ReadAsync(WebSocket socket, Action validate, CancellationToken token)
    {
        using var data = new MemoryStream(); var buffer = new byte[8192]; var fragments = 0; var started = false;
        while (true)
        {
            validate(); token.ThrowIfCancellationRequested(); var read = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); validate();
            if (read.Count < 0 || read.Count > buffer.Length) throw new InvalidDataException("星火 WebSocket 返回无效读取长度。");
            if (read.MessageType == WebSocketMessageType.Close)
            {
                if (started || read.Count != 0) throw new InvalidDataException("星火 JSON 消息在分片完成前关闭。"); return (null, read.CloseStatus ?? socket.CloseStatus);
            }
            if (read.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("星火原生模型消息必须是 WebSocket 文本。");
            if (fragments >= options.MaximumFragmentsPerMessage || read.Count > options.MaximumResponseBytes - _total || read.Count > options.MaximumMessageBytes - data.Length) throw new InvalidDataException("星火消息分片或原生字节超过上限。");
            fragments++; started = true; _total += read.Count; data.Write(buffer, 0, read.Count);
            if (!read.EndOfMessage) continue;
            if (_events >= options.MaximumEvents) throw new InvalidDataException("星火原生消息数超过上限。"); _events++; return (data.ToArray(), null);
        }
    }
}
