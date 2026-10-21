using System.Security.Cryptography;

namespace Asgard.Loki.LLM.Tencent.Internal;

/// <summary>单次原生请求的实际目标、正文和签名头快照；只借用消息，不拥有工厂或响应。</summary>
internal sealed class TencentWireGuard
{
    private readonly HttpRequestMessage _message;
    private readonly HttpContent _content;
    private readonly Uri _endpoint;
    private readonly byte[] _hash;
    private readonly Dictionary<string, string> _headers;
    /// <summary>签名后捕获一次最终原生字节和头；不重序列化请求或记录认证材料。</summary>
    internal TencentWireGuard(HttpRequestMessage message, byte[] body)
    {
        _message = message; _content = message.Content!; _endpoint = message.RequestUri!; _hash = SHA256.HashData(body);
        _headers = message.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
    }
    /// <summary>响应头、源读取/关闭、客户端关闭后的实际请求仍必须与同一个签名快照一致。</summary>
    internal async ValueTask VerifyAsync(HttpResponseMessage response, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_message.Method != HttpMethod.Post || _message.RequestUri != _endpoint || response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri != _endpoint)
            || !ReferenceEquals(_message.Content, _content) || _content.Headers.ContentType?.ToString() != "application/json"
            || _headers.Any(h => !_message.Headers.TryGetValues(h.Key, out var values) || string.Join(",", values) != h.Value)
            || _message.Headers.Any(h => !_headers.ContainsKey(h.Key) && (h.Key.StartsWith("X-TC-", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)))) throw new InvalidDataException("混元原生实际目标、正文或认证头发生改变。");
        if (!SHA256.HashData(await _content.ReadAsByteArrayAsync(token).ConfigureAwait(false)).SequenceEqual(_hash)) throw new InvalidDataException("混元原生实际正文与签名快照不一致。");
        token.ThrowIfCancellationRequested();
    }
}
