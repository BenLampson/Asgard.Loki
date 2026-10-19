using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Audio.OpenAI.Internal;

namespace Asgard.Loki.Audio.OpenAI;

/// <summary>原生 OpenAI 二进制语音调用，支持便捷和原生 JSON 入口，不合成 SSE 或自动重试。</summary>
public sealed class OpenAiSpeechAdapter : INativeSpeechAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly OpenAiSpeechOptions _options;
    private readonly Uri _endpoint;

    /// <summary>校验完整目标，不查询模型/声音或读取其他能力凭据。</summary>
    public OpenAiSpeechAdapter(IHttpClientFactory factory, OpenAiSpeechOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id); ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey); ArgumentException.ThrowIfNullOrWhiteSpace(options.HttpClientName);
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("https" or "http")
            || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0)
            throw new ArgumentException("语音 API 根地址必须为无凭据、查询或片段的 HTTP(S) 绝对地址。", nameof(options));
        if (options.ReadBufferSize is < 1 or > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(options), "本地缓冲区必须为 1 至 1 MiB。");
        _endpoint = new(new Uri(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"), "audio/speech"); _factory = factory; _options = options;
    }
    /// <inheritdoc />
    public string Id => _options.Id;
    /// <inheritdoc />
    public IAsyncEnumerable<SpeechChunk> StreamAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return StreamPayloadAsync(OpenAiSpeechPayload.Create(request, _options.Model), cancellationToken); }
    /// <inheritdoc />
    public IAsyncEnumerable<SpeechChunk> StreamNativeAsync(NativeSpeechRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return StreamPayloadAsync(OpenAiSpeechPayload.Create(request, _options.Model), cancellationToken); }

    /// <summary>逐次复制原始字节，正常 EOF/长度校验及全部传输资源释放成功后才发布完成块。</summary>
    private async IAsyncEnumerable<SpeechChunk> StreamPayloadAsync((JsonObject Body, string? Format, string PublicModel) payload,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        SpeechChunk terminal;
        using (var client = _factory.CreateClient(_options.HttpClientName))
        using (var message = new HttpRequestMessage(HttpMethod.Post, _endpoint))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream")); message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));
            if (_options.Organization is not null) message.Headers.Add("OpenAI-Organization", _options.Organization);
            if (_options.Project is not null) message.Headers.Add("OpenAI-Project", _options.Project);
            message.Content = new StringContent(payload.Body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var media = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode || media is not null && (media.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || media.EndsWith("+json", StringComparison.OrdinalIgnoreCase) || media.StartsWith("text/", StringComparison.OrdinalIgnoreCase) && !media.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase)))
                throw new SpeechUpstreamException("OpenAI 语音返回了 HTTP/非音频错误正文。", (int)response.StatusCode,
                    await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (string.Equals(media, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("当前二进制语音适配器不消费 SSE 事件。");
            OpenAiSpeechTransport.ValidateContentType(media, payload.Format);
            var expected = response.Content.Headers.ContentLength;
            if (response.Content.Headers.Contains("Content-Length") && expected is null) throw new InvalidDataException("无效的原生 Content-Length。");
            var metadata = OpenAiSpeechTransport.Metadata(response, payload.PublicModel, _options.Model);
            long received = 0;
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                var buffer = new byte[_options.ReadBufferSize]; int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested(); received = checked(received + read);
                    if (expected is not null && received > expected) throw new InvalidDataException("语音正文超过声明的 Content-Length。");
                    yield return new SpeechChunk(buffer.AsMemory(0, read), false, OpenAiSpeechTransport.Frame(metadata, received, false), Id);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (received == 0 || expected is not null && received != expected) throw new InvalidDataException("语音正文为空或未完整接收声明长度。");
                terminal = new(ReadOnlyMemory<byte>.Empty, true, OpenAiSpeechTransport.Frame(metadata, received, true), Id);
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); yield return terminal;
    }
}
