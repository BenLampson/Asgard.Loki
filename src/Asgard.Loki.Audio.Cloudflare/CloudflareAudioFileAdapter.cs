using System.Net.Http.Headers;
using System.Text.Json;
using Asgard.Loki.Audio.Cloudflare.Internal;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Audio.Cloudflare;

/// <summary>固定账户和模型的原生音频识别/翻译调用，不切换输入结构或执行重试。</summary>
public sealed class CloudflareAudioFileAdapter : IAudioTranscriptionAdapter, IAudioTranslationAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly CloudflareAudioFileOptions _options;
    private readonly Uri _endpoint;
    /// <summary>只校验明确配置，不访问账号、模型目录、其他能力或真实网络。</summary>
    public CloudflareAudioFileAdapter(IHttpClientFactory factory, CloudflareAudioFileOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var text in new[] { options.Id, options.AccountId, options.Model, options.ApiToken, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(text); CloudflareAudioJson.Text(text); }
        if (options.ApiToken.Any(char.IsControl)) throw new ArgumentException("Cloudflare 令牌含无效头部字符。");
        if (!Enum.IsDefined(options.InputFormat) || options.MaxUploadBytes <= 0 || options.MaxParameterBytes <= 0 || options.MaxResponseBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("https" or "http")
            || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0)
            throw new ArgumentException("Cloudflare API 根地址必须为无凭据、查询参数和片段的 HTTP(S) 地址。");
        Segment(options.AccountId);
        var segments = options.Model.Split('/'); foreach (var segment in segments) Segment(segment);
        var model = string.Join('/', segments.Select(segment => Uri.EscapeDataString(segment).Replace("%40", "@", StringComparison.Ordinal)));
        _endpoint = new(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/accounts/" + Uri.EscapeDataString(options.AccountId) + "/ai/run/" + model);
        _factory = factory; _options = options;
    }
    /// <inheritdoc />
    public string Id => _options.Id;
    /// <inheritdoc />
    public Task<AudioFileResult> TranscribeAsync(AudioFileRequest request, CancellationToken cancellationToken = default)
        => SendAsync(CloudflareAudioPayload.Prepare(request, _options, false, cancellationToken), cancellationToken);
    /// <inheritdoc />
    public Task<AudioFileResult> TranslateAsync(AudioFileRequest request, CancellationToken cancellationToken = default)
        => SendAsync(CloudflareAudioPayload.Prepare(request, _options, true, cancellationToken), cancellationToken);

    private async Task<AudioFileResult> SendAsync(CloudflareAudioPayload request, CancellationToken token)
    {
        AudioFileResult result;
        using (HttpContent content = request.Parameters is { } parameters
            ? new CloudflareAudioJsonContent(request.Source, parameters, _options.MaxUploadBytes)
            : new BorrowedStreamContent(request.Source, _options.MaxUploadBytes))
        using (var client = _factory.CreateClient(_options.HttpClientName))
        using (var message = new HttpRequestMessage(HttpMethod.Post, _endpoint))
        {
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.Parameters is null ? request.RawContentType : "application/json");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken); message.Content = content;
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var raw = await HttpBodyReader.ReadAsync(response.Content, _options.MaxResponseBytes, token).ConfigureAwait(false);
            if (response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri != _endpoint))
                throw new InvalidDataException("Cloudflare 音频响应没有保持固定的请求目标和方法。");
            var mediaType = response.Content.Headers.ContentType; var contentType = mediaType?.ToString();
            if (!response.IsSuccessStatusCode) throw new AudioFileUpstreamException("Cloudflare 音频 HTTP 调用失败。", (int)response.StatusCode, raw, contentType);
            if (!string.Equals(mediaType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                || mediaType?.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Cloudflare 原生音频没有返回 UTF-8 JSON。");
            var native = CloudflareAudioJson.Parse(raw);
            if (!native.TryGetProperty("success", out var success) || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !native.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Cloudflare 音频缺少有效原生状态信封。");
            if (!success.GetBoolean() || errors.GetArrayLength() != 0)
                throw new AudioFileUpstreamException("Cloudflare 原生音频执行失败。", (int)response.StatusCode, raw, contentType);
            CloudflareAudioJson.Result(native);
            result = new(raw, contentType!, new(request.PublicModel, _options.Model, Id));
        }
        token.ThrowIfCancellationRequested(); return result;
    }
    private static void Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Any(character => char.IsWhiteSpace(character)
            || char.IsControl(character) || character is '/' or '\\' or '%' or '?' or '#'))
            throw new ArgumentException("Cloudflare 账户或模型路径段无效。");
    }
}
