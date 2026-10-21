using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Asgard.Loki.Audio.Volcengine.Internal;

/// <summary>单次调用拥有连接及终态；不复用会话、不重连、不执行后台任务。</summary>
internal sealed class V1SpeechWebSocketCall(VolcengineV1SpeechOptions options, Uri endpoint, IVolcengineV1SpeechWebSocketFactory factory)
{
    /// <summary>末包及其后元数据延迟到正常关闭、释放和取消检查完成后输出。</summary>
    internal async IAsyncEnumerable<SpeechChunk> StreamAsync(V1SpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var terminal = new List<SpeechChunk>(); long rawTotal = 0, decodedTotal = 0, audioTotal = 0; var count = 0;
        using (var socket = await factory.ConnectAsync(endpoint,
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string> { ["Authorization"] = "Bearer;" + options.AccessToken }),
            cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outgoing = V1SpeechCodec.Encode(request.Bytes, options.CompressRequest);
            await socket.SendAsync(new ArraySegment<byte>(outgoing), WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
            using var closing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var final = false;
            while (true)
            {
                var raw = await ReceiveAsync(socket, final ? closing.Token : cancellationToken).ConfigureAwait(false);
                if (raw is null)
                {
                    if (!final || socket.CloseStatus != WebSocketCloseStatus.NormalClosure)
                        throw new InvalidDataException("V1 WebSocket 未以原生末包和正常关闭结束。");
                    break;
                }
                if (++count > options.MaxFrames || raw.Length > options.MaxResponseBytes - rawTotal)
                    throw new InvalidDataException("V1 响应帧数或线上累计字节超过上限。");
                rawTotal += raw.Length;
                var packet = V1SpeechCodec.Decode(raw, options.MaxDecodedBytes);
                if (packet.Payload.Length > options.MaxResponseBytes - decodedTotal) throw new InvalidDataException("V1 解码累计字节超过上限。");
                decodedTotal += packet.Payload.Length;
                if (packet.ErrorCode is { } code) throw new VolcengineV1SpeechException(code, packet.Raw, packet.Payload);
                if (packet.NativeData is { ValueKind: JsonValueKind.Object } native && native.TryGetProperty("reqid", out _)
                    && V1SpeechJson.Required(native, "reqid") != request.RequestId)
                    throw new InvalidDataException("V1 元数据请求标识不匹配。");
                if (final && packet.Type == 11) throw new InvalidDataException("V1 末包后出现额外音频报文。");
                var audio = packet.Type == 11 ? packet.Payload : Array.Empty<byte>(); audioTotal += audio.Length;
                var chunk = new SpeechChunk(audio, false, packet.Metadata(request.RequestId), options.Id);
                if (packet.Sequence < 0)
                {
                    if (audioTotal == 0) throw new InvalidDataException("V1 完成调用没有音频数据。");
                    final = true; closing.CancelAfter(options.CloseTimeout);
                    terminal.Add(chunk);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closing.Token).ConfigureAwait(false);
                }
                else if (final) terminal.Add(chunk);
                else yield return chunk;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        for (var index = 0; index < terminal.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = terminal[index];
            yield return new SpeechChunk(chunk.Audio, index == terminal.Count - 1, chunk.Data, chunk.ProviderId);
        }
    }

    private async Task<byte[]?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(); var buffer = new byte[Math.Min(8192, options.MaxMessageBytes)]; var fragments = 0;
        while (true)
        {
            if (++fragments > options.MaxFrames) throw new InvalidDataException("V1 单条消息的分片数超过上限。");
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (output.Length != 0) throw new InvalidDataException("V1 二进制报文中途关闭。");
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Binary || result.Count > options.MaxMessageBytes - output.Length)
                throw new InvalidDataException("V1 响应不是有界二进制消息。");
            output.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return output.ToArray();
        }
    }
}
