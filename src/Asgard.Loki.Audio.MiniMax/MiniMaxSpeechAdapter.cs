using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Asgard.Loki.Audio.MiniMax.Internal;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Audio.MiniMax;

/// <summary>单次原生同步 HTTP 语音调用，保留完整结果且不获取远程音频或字幕 URL。</summary>
public sealed class MiniMaxSpeechAdapter : INativeSpeechAdapter
{
    private readonly IHttpClientFactory _http;
    private readonly MiniMaxSpeechOptions _options;
    /// <summary>校验固定目标，不访问网络；HTTP 工厂保持借用关系。</summary>
    public MiniMaxSpeechAdapter(IHttpClientFactory httpClientFactory, MiniMaxSpeechOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory); ArgumentNullException.ThrowIfNull(options);
        foreach (var text in new[] { options.Id, options.Model, options.ApiKey, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(text); MiniMaxSpeechJson.Text(text); }
        if (options.ApiKey.Any(char.IsControl)) throw new ArgumentException("语音凭据包含无效请求头字符。", nameof(options));
        ArgumentNullException.ThrowIfNull(options.Endpoint);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme is not ("https" or "http")
            || options.Endpoint.UserInfo.Length != 0 || options.Endpoint.Query.Length != 0 || options.Endpoint.Fragment.Length != 0)
            throw new ArgumentException("语音接口必须为无凭据、查询参数和片段的完整 HTTP(S) 地址。", nameof(options));
        if (options.MaxRequestBytes <= 0 || options.MaxResponseBytes <= 0 || options.MaxAudioBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _http = httpClientFactory; _options = options;
    }
    /// <inheritdoc />
    public string Id => _options.Id;
    /// <inheritdoc />
    public IAsyncEnumerable<SpeechChunk> StreamNativeAsync(NativeSpeechRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync(MiniMaxSpeechRequest.Prepare(request, _options), cancellationToken);
    }
    /// <inheritdoc />
    public IAsyncEnumerable<SpeechChunk> StreamAsync(SpeechRequest request, CancellationToken cancellationToken = default)
        => StreamNativeAsync(MiniMaxSpeechRequest.Bridge(request), cancellationToken);
    private async IAsyncEnumerable<SpeechChunk> ExecuteAsync(MiniMaxSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        SpeechChunk terminal;
        using (var client = _http.CreateClient(_options.HttpClientName))
        using (var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            message.Content = new ByteArrayContent(request.Bytes); message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var raw = await HttpBodyReader.ReadAsync(response.Content, _options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.ToString();
            if (!response.IsSuccessStatusCode) throw new MiniMaxSpeechException(response.StatusCode, null, raw, contentType);
            var root = MiniMaxSpeechJson.Parse(raw);
            if (!root.TryGetProperty("base_resp", out var status)) throw new InvalidDataException("MiniMax 响应缺少原生状态。");
            var code = MiniMaxSpeechJson.Number(status, "status_code");
            if (code != 0) throw new MiniMaxSpeechException(response.StatusCode, code, raw, contentType, root);
            if (!root.TryGetProperty("data", out var data) || MiniMaxSpeechJson.Number(data, "status") != 2)
                throw new InvalidDataException("MiniMax 同步响应没有原生合成完成状态。");
            var original = MiniMaxSpeechJson.Required(data, "audio");
            byte[] audio;
            if (request.UrlOutput)
            {
                if (!Uri.TryCreate(original, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
                    || uri.UserInfo.Length != 0 || original.Any(char.IsWhiteSpace) || original.Any(char.IsControl))
                    throw new InvalidDataException("MiniMax URL 音频结果不是有效的原生 HTTP(S) 地址。");
                audio = []; // URL 只保留在完整原生 JSON；调用者显式获取。
            }
            else
            {
                if ((original.Length & 1) != 0 || original.Length / 2 > _options.MaxAudioBytes) throw new InvalidDataException("MiniMax hex 音频无效或超过上限。");
                try { audio = Convert.FromHexString(original); }
                catch (FormatException error) { throw new InvalidDataException("MiniMax hex 音频无效。", error); }
            }
            terminal = new(audio, true, root, Id);
        }
        cancellationToken.ThrowIfCancellationRequested(); yield return terminal;
    }
}
