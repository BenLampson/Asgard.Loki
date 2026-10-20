using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Asgard.Loki.Audio.Volcengine.Internal;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Audio.Volcengine;

/// <summary>显式 V1 HTTP 或二进制 WebSocket 语音调用；V3 使用独立入口。</summary>
public sealed class VolcengineV1SpeechAdapter : INativeSpeechAdapter
{
    private readonly VolcengineV1SpeechOptions _options;
    private readonly Uri _endpoint;
    private readonly IHttpClientFactory? _http;
    private readonly IVolcengineV1SpeechWebSocketFactory _sockets;
    /// <summary>校验固定目标与本地资源策略，不连接网络；借用的工厂不由本实例释放。</summary>
    public VolcengineV1SpeechAdapter(VolcengineV1SpeechOptions options, IHttpClientFactory? httpClientFactory = null,
        IVolcengineV1SpeechWebSocketFactory? webSocketFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var value in new[] { options.Id, options.AppId, options.Cluster, options.AccessToken, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(value); V1SpeechJson.Text(value); }
        if (options.AccessToken.Any(char.IsControl)) throw new ArgumentException("访问令牌包含无效请求头字符。", nameof(options));
        if (!Enum.IsDefined(options.Transport)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Transport == VolcengineV1SpeechTransport.Http && httpClientFactory is null) throw new ArgumentNullException(nameof(httpClientFactory));
        if (options.Transport == VolcengineV1SpeechTransport.Http && options.CompressRequest) throw new ArgumentException("请求 gzip 仅适用于 V1 WebSocket。", nameof(options));
        _endpoint = options.Endpoint ?? new Uri(options.Transport == VolcengineV1SpeechTransport.Http
            ? "https://openspeech.bytedance.com/api/v1/tts" : "wss://openspeech.bytedance.com/api/v1/tts/ws_binary");
        var schemeValid = options.Transport == VolcengineV1SpeechTransport.Http
            ? _endpoint.IsAbsoluteUri && _endpoint.Scheme is "https" or "http"
            : _endpoint.IsAbsoluteUri && _endpoint.Scheme is "wss" or "ws";
        if (!schemeValid || _endpoint.UserInfo.Length != 0 || _endpoint.Query.Length != 0 || _endpoint.Fragment.Length != 0)
            throw new ArgumentException("V1 地址必须匹配显式传输且不能含凭据、查询参数或片段。", nameof(options));
        if (options.MaxRequestBytes <= 0 || options.MaxMessageBytes < 12 || options.MaxDecodedBytes <= 0
            || options.MaxResponseBytes <= 0 || options.MaxFrames <= 0 || options.CloseTimeout <= TimeSpan.Zero
            || options.CloseTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(options), "V1 资源上限必须为有效正值。");
        _options = options; _http = httpClientFactory; _sockets = webSocketFactory ?? new DefaultV1SpeechWebSocketFactory();
    }
    /// <inheritdoc />
    public string Id => _options.Id;
    /// <inheritdoc />
    public IAsyncEnumerable<SpeechChunk> StreamNativeAsync(NativeSpeechRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = V1SpeechRequest.Prepare(request, _options);
        return _options.Transport == VolcengineV1SpeechTransport.Http ? HttpAsync(prepared, cancellationToken)
            : new V1SpeechWebSocketCall(_options, _endpoint, _sockets).StreamAsync(prepared, cancellationToken);
    }
    /// <inheritdoc />
    public IAsyncEnumerable<SpeechChunk> StreamAsync(SpeechRequest request, CancellationToken cancellationToken = default)
        => StreamNativeAsync(V1SpeechRequest.Bridge(request), cancellationToken);

    private async IAsyncEnumerable<SpeechChunk> HttpAsync(V1SpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        SpeechChunk terminal;
        using (var client = _http!.CreateClient(_options.HttpClientName))
        using (var message = new HttpRequestMessage(HttpMethod.Post, _endpoint))
        {
            message.Headers.TryAddWithoutValidation("Authorization", "Bearer;" + _options.AccessToken);
            message.Content = new ByteArrayContent(request.Bytes);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var raw = await HttpBodyReader.ReadAsync(response.Content, _options.MaxMessageBytes, cancellationToken).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.ToString();
            if (!response.IsSuccessStatusCode) throw new VolcengineV1SpeechException(null, raw, raw, response.StatusCode, contentType);
            var root = V1SpeechJson.Parse(raw);
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var codeValue)
                || codeValue.ValueKind != JsonValueKind.Number || !codeValue.TryGetInt64(out var code))
                throw new InvalidDataException("V1 HTTP 响应缺少有效业务码。");
            if (code != 3000) throw new VolcengineV1SpeechException(code, raw, raw, response.StatusCode, contentType);
            if (V1SpeechJson.Required(root, "reqid") != request.RequestId) throw new InvalidDataException("V1 HTTP 请求标识不匹配。");
            if (!root.TryGetProperty("sequence", out var sequence) || sequence.ValueKind != JsonValueKind.Number
                || !sequence.TryGetInt32(out var number) || number >= 0)
                throw new InvalidDataException("V1 HTTP 缺少负序号完成标记。");
            var base64 = V1SpeechJson.Required(root, "data");
            var audio = new byte[(int)Math.Min(_options.MaxDecodedBytes, (long)base64.Length / 4 * 3)];
            if (!Convert.TryFromBase64String(base64, audio, out var length) || length == 0)
                throw new InvalidDataException("V1 HTTP 音频 Base64 无效、为空或超过上限。");
            terminal = new SpeechChunk(audio.AsMemory(0, length), true, root, Id);
        }
        cancellationToken.ThrowIfCancellationRequested();
        yield return terminal;
    }
}
