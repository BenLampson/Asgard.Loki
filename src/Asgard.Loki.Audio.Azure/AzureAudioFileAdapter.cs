using System.Net;
using System.Text.Json;
using Asgard.Loki.Audio.Azure.Internal;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Audio.Azure;

/// <summary>部署级原生文件转录/翻译，保留完整 JSON/文本结果，不重试、改语言或执行文件管理。</summary>
public sealed class AzureAudioFileAdapter : IAudioTranscriptionAdapter, IAudioTranslationAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly AzureAudioTarget _target;
    /// <summary>只借用工厂并验证本完整目标，不查询模型或复用其他能力的身份。</summary>
    public AzureAudioFileAdapter(IHttpClientFactory factory, AzureAudioOptions options)
    { ArgumentNullException.ThrowIfNull(factory); _factory = factory; _target = new(options); }
    /// <inheritdoc />
    public string Id => _target.Options.Id;
    /// <inheritdoc />
    public Task<AudioFileResult> TranscribeAsync(AudioFileRequest request, CancellationToken cancellationToken = default)
        => SendAsync(AzureAudioFilePayload.Prepare(request, _target.Options, cancellationToken), "transcriptions", cancellationToken);
    /// <inheritdoc />
    public Task<AudioFileResult> TranslateAsync(AudioFileRequest request, CancellationToken cancellationToken = default)
        => SendAsync(AzureAudioFilePayload.Prepare(request, _target.Options, cancellationToken), "translations", cancellationToken);

    private async Task<AudioFileResult> SendAsync(AzureAudioFilePayload payload, string operation, CancellationToken token)
    {
        var options = _target.Options; AudioFileResult result;
        using (var content = payload.Content(options))
        using (var client = _factory.CreateClient(options.HttpClientName))
        using (var message = _target.Message(operation))
        {
            message.Content = content;
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            _target.ValidateResponse(response, operation);
            if (response.Content.Headers.Contains("Content-Length") && response.Content.Headers.ContentLength is null)
                throw new InvalidDataException("Azure 文件响应声明长度无效。");
            var bytes = await HttpBodyReader.ReadAsync(response.Content, options.MaxResponseBytes, token).ConfigureAwait(false);
            var type = response.Content.Headers.ContentType; var media = type?.MediaType; var originalType = type?.ToString();
            if (response.StatusCode != HttpStatusCode.OK) throw new AzureAudioException((int)response.StatusCode, bytes, originalType);
            var json = string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase) || media?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true;
            var text = media?.ToLowerInvariant() is "text/plain" or "text/vtt" or "text/srt" or "application/x-subrip" or "application/srt";
            if (!json && !text) throw new InvalidDataException("Azure 部署文件入口没有返回支持的完整 JSON/文本类型。");
            if (type?.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Azure 文件响应声明了不支持的字符集。");
            if (json)
            {
                var native = AzureAudioJson.Parse(bytes);
                if (native.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) throw new AzureAudioException((int)response.StatusCode, bytes, originalType);
                if (!native.TryGetProperty("text", out var transcript) || transcript.ValueKind != JsonValueKind.String) throw new InvalidDataException("Azure 文件 JSON 缺少原生 text。");
                if (payload.Format is "text" or "srt" or "vtt") throw new InvalidDataException("Azure 返回 JSON 与显式文本格式不符。");
            }
            else
            {
                AzureAudioJson.Plain(bytes);
                if (payload.Format is "json" or "verbose_json" or "diarized_json") throw new InvalidDataException("Azure 返回文本与显式 JSON 格式不符。");
            }
            result = new(bytes, originalType!, new(payload.Request.Model, options.Deployment, Id));
        }
        token.ThrowIfCancellationRequested(); return result;
    }
}
