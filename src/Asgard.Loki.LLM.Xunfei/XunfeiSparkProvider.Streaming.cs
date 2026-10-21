using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Xunfei.Internal;

namespace Asgard.Loki.LLM.Xunfei;

/// <summary>星火始终使用原生 WebSocket；全局/文本终态之后主动握手关闭，释放后才交付原生尾部。</summary>
public sealed partial class XunfeiSparkProvider
{
    /// <inheritdoc />
    public async IAsyncEnumerable<SparkEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var token = cancellationToken; token.ThrowIfCancellationRequested(); ValidateContext(context); var body = SparkPayload.Snapshot(request, context.Target.UpstreamModel, _options); ConfigurationUnchanged();
        var credentials = await _source.GetCredentialsAsync(context.Credential.PrincipalIdentity, token).ConfigureAwait(false); ArgumentNullException.ThrowIfNull(credentials); ValidateIdentity(credentials, context, token);
        var signed = SparkSigner.Sign(context.Target.BaseAddress, credentials, _time.GetUtcNow()); WebSocket? socket = null; var state = new SparkState(); var receiver = new SparkReceiver(_options); List<SparkEvent> pending = [];
        try
        {
            using (var deadline = new CancellationTokenSource(_options.ConnectTimeout, _time))
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token))
            {
                socket = await _factory.ConnectAsync(signed, connect.Token).ConfigureAwait(false); ArgumentNullException.ThrowIfNull(socket); ValidateIdentity(credentials, context, connect.Token);
            }
            if (socket.State != WebSocketState.Open) throw new InvalidDataException("星火工厂未返回可用的独立连接。");
            var hash = SHA256.HashData(body); await socket.SendAsync(body.AsMemory(), WebSocketMessageType.Text, true, token).ConfigureAwait(false); ValidateIdentity(credentials, context, token);
            if (!SHA256.HashData(body).SequenceEqual(hash)) throw new InvalidDataException("星火发送正文在传输等待期间改变。");
            while (!state.Ended)
            {
                var read = await receiver.ReadAsync(socket, () => ValidateIdentity(credentials, context, token), token).ConfigureAwait(false);
                if (read.Body is null) throw new InvalidDataException("星火在原生全局终态之前关闭连接。");
                var data = SparkState.Parse(read.Body); state.Observe(data); var item = new SparkEvent(data, context.Metadata);
                if (state.Finishing) { if (pending.Count >= _options.MaximumBufferedTerminalEvents) throw new InvalidDataException("星火原生终态窗口超过上限。"); pending.Add(item); }
                else { ValidateIdentity(credentials, context, token); yield return item; }
            }
            state.Complete();
            using (var deadline = new CancellationTokenSource(_options.CloseTimeout, _time))
            using (var closing = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token))
            {
                ValidateIdentity(credentials, context, closing.Token); await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closing.Token).ConfigureAwait(false); ValidateIdentity(credentials, context, closing.Token);
                var ack = await receiver.ReadAsync(socket, () => ValidateIdentity(credentials, context, closing.Token), closing.Token).ConfigureAwait(false);
                if (ack.Body is not null) { _ = SparkState.Parse(ack.Body); throw new InvalidDataException("星火全局终态后追加原生数据。"); }
                if (ack.Close != WebSocketCloseStatus.NormalClosure) throw new InvalidDataException("星火连接没有正常确认关闭。");
                ValidateIdentity(credentials, context, closing.Token);
            }
        }
        finally { socket?.Dispose(); }
        ValidateIdentity(credentials, context, token);
        foreach (var item in pending) { ValidateIdentity(credentials, context, token); yield return item; }
        ValidateIdentity(credentials, context, token);
    }
}
