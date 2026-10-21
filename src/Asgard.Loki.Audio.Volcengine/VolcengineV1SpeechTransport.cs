namespace Asgard.Loki.Audio.Volcengine;

/// <summary>显式选择 V1 原生传输，不自动回退或切换协议。</summary>
public enum VolcengineV1SpeechTransport
{
    /// <summary>一次性 HTTP query 调用。</summary>
    Http,
    /// <summary>单次 WebSocket submit 调用。</summary>
    WebSocket
}
