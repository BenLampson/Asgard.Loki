using System.Net.Http.Headers;
using Asgard.Loki.Audio.OpenAI.Internal;

namespace Asgard.Loki.Audio.OpenAI;

/// <summary>OpenAI 原生文件转录/翻译 multipart 调用，不附带流式、自动重试或其他能力协调。</summary>
public sealed class OpenAiAudioFileAdapter : IAudioTranscriptionAdapter, IAudioTranslationAdapter
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

    /// <summary>实际模型绑定到 multipart，所有传输资源成功释放后才返回完整原生结果。</summary>
    private async Task<AudioFileResult> SendAsync((AudioFileRequest Request, string? Format) payload, string path, CancellationToken token)
    {
        AudioFileResult result;
        using (var content = OpenAiAudioFilePayload.Create(payload.Request, _options.Model))
        using (var client = _factory.CreateClient(_options.HttpClientName))
        using (var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_root, path)))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            if (_options.Organization is not null) message.Headers.Add("OpenAI-Organization", _options.Organization);
            if (_options.Project is not null) message.Headers.Add("OpenAI-Project", _options.Project);
            message.Content = content;
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            OpenAiAudioFileResponse.ValidateHeaders(response);
            var body = await OpenAiAudioFileResponse.ReadAsync(response, token).ConfigureAwait(false);
            OpenAiAudioFileResponse.Validate(response, body, payload.Format);
            result = new(body, response.Content.Headers.ContentType!.ToString(), new(payload.Request.Model, _options.Model, Id));
        }
        token.ThrowIfCancellationRequested(); return result;
    }
}
