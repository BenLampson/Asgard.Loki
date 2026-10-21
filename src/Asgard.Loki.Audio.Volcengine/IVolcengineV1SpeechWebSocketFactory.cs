using System.Net.WebSockets;

namespace Asgard.Loki.Audio.Volcengine;

/// <summary>借用的连接工厂；每次返回独立连接，其所有权交给单次语音调用。</summary>
public interface IVolcengineV1SpeechWebSocketFactory
{
    /// <summary>仅连接指定目标一次，不重试或发现其他凭据。</summary>
    Task<WebSocket> ConnectAsync(Uri address, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default);
}
