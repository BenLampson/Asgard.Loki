using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using Asgard.Loki.Audio.OpenAI.Internal;

namespace Asgard.Loki.Audio.OpenAI;

/// <summary>OpenAI 原生文件转录 JSON/SSE 与翻译 multipart 调用，不自动重试或协调其他能力。</summary>
public sealed class OpenAiAudioFileAdapter : IStreamingAudioTranscriptionAdapter, IAudioTranslationAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly OpenAiAudioFileOptions _options;
    private readonly Uri _root;
    /// <summary>校验调用者明确提供的完整目标，不读取其他项目/能力的凭据或模型列表。</summary>
    public OpenAiAudioFileAdapter(IHttpClientFactory factory, OpenAiAudioFileOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id); ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey); ArgumentException.ThrowIfNullOrWhiteSpace(options.HttpClientName);
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxTrackedTranscriptionSegments);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("http" or "https") || options.BaseAddress.UserInfo.Length != 0
            || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0) throw new ArgumentException("文件 API 根地址必须为无凭据/查询/片段的 HTTP(S) 绝对地址。");
        _root = new(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"); _factory = factory; _options = options;
    }
    /// <inheritdoc />
    public string Id => _options.Id;
    /// <inheritdoc />
    public Task<AudioFileResult> TranscribeAsync(AudioFileRequest request, CancellationToken cancellationToken = default)
        => SendAsync(OpenAiAudioFilePayload.Snapshot(request, cancellationToken), "audio/transcriptions", cancellationToken);
    /// <inheritdoc />
    public Task<AudioFileResult> TranslateAsync(AudioFileRequest request, CancellationToken cancellationToken = default)
        => SendAsync(OpenAiAudioFilePayload.Snapshot(request, cancellationToken), "audio/translations", cancellationToken);
    /// <inheritdoc />
    public IAsyncEnumerable<AudioTranscriptionEvent> StreamAsync(AudioFileRequest request, CancellationToken cancellationToken = default)
        => StreamPayloadAsync(OpenAiAudioFilePayload.Snapshot(request, cancellationToken, streaming: true), cancellationToken);

    /// <summary>单次上传只返回原生转录事件；完成项在读取尾部和释放所有传输资源后才可见。</summary>
    private async IAsyncEnumerable<AudioTranscriptionEvent> StreamPayloadAsync((AudioFileRequest Request, string? Format) payload,
        [EnumeratorCancellation] CancellationToken token)
    {
        AudioTranscriptionEvent? terminal = null;
        using (var content = OpenAiAudioFilePayload.Create(payload.Request, _options.Model))
        using (var client = _factory.CreateClient(_options.HttpClientName))
        using (var message = CreateMessage(content, "audio/transcriptions"))
        {
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || !string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                OpenAiAudioFileResponse.ValidateHeaders(response);
                var body = await OpenAiAudioFileResponse.ReadAsync(response, token).ConfigureAwait(false);
                OpenAiAudioFileResponse.Validate(response, body, payload.Format);
                throw new InvalidDataException("原生转录流没有返回 SSE，不能将普通文件结果伪装成事件流。");
            }
            var expected = response.Content.Headers.ContentLength;
            if (response.Content.Headers.Contains("Content-Length") && expected is null) throw new InvalidDataException("转录 SSE 的 Content-Length 无效。");
            await using (var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            {
                var metadata = new AudioResultMetadata(payload.Request.Model, _options.Model, Id);
                await foreach (var item in OpenAiTranscriptionSse.ReadAsync(stream, expected, metadata, (int)response.StatusCode,
                    response.Content.Headers.ContentType?.ToString(), _options.MaxTrackedTranscriptionSegments, token).ConfigureAwait(false))
                { if (item.IsCompleted) terminal = item; else yield return item; }
            }
        }
        token.ThrowIfCancellationRequested(); yield return terminal ?? throw new InvalidDataException("转录 SSE 没有有效完成项。");
    }

    /// <summary>实际模型绑定到 multipart，所有传输资源成功释放后才返回完整原生结果。</summary>
    private async Task<AudioFileResult> SendAsync((AudioFileRequest Request, string? Format) payload, string path, CancellationToken token)
    {
        AudioFileResult result;
        using (var content = OpenAiAudioFilePayload.Create(payload.Request, _options.Model))
        using (var client = _factory.CreateClient(_options.HttpClientName))
        using (var message = CreateMessage(content, path))
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            OpenAiAudioFileResponse.ValidateHeaders(response);
            var body = await OpenAiAudioFileResponse.ReadAsync(response, token).ConfigureAwait(false);
            OpenAiAudioFileResponse.Validate(response, body, payload.Format);
            result = new(body, response.Content.Headers.ContentType!.ToString(), new(payload.Request.Model, _options.Model, Id));
        }
        token.ThrowIfCancellationRequested(); return result;
    }

    /// <summary>所有文件入口使用同一个固定目标；身份头只来自调用者配置。</summary>
    private HttpRequestMessage CreateMessage(HttpContent content, string path)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_root, path));
        try
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            if (_options.Organization is not null) message.Headers.Add("OpenAI-Organization", _options.Organization);
            if (_options.Project is not null) message.Headers.Add("OpenAI-Project", _options.Project);
            message.Content = content; return message;
        }
        catch { message.Dispose(); throw; }
    }
}
