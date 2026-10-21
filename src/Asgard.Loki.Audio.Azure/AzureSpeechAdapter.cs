using System.Net.Http.Headers;
using System.Net;
using System.Runtime.CompilerServices;
using Asgard.Loki.Audio.Azure.Internal;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Audio.Azure;

/// <summary>部署级 Azure 原生 JSON 到二进制语音，成功需完整读取、释放及最终取消检查。</summary>
public sealed class AzureSpeechAdapter : INativeSpeechAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly AzureAudioTarget _target;
    /// <summary>校验固定资源/部署/版本/API Key，不与其他能力共享业务流程或凭据。</summary>
    public AzureSpeechAdapter(IHttpClientFactory factory, AzureAudioOptions options)
    { ArgumentNullException.ThrowIfNull(factory); _factory = factory; _target = new(options); }
    /// <inheritdoc />
    public string Id => _target.Options.Id;
    /// <inheritdoc />
    public IAsyncEnumerable<SpeechChunk> StreamNativeAsync(NativeSpeechRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return SendAsync(AzureSpeechPayload.Prepare(request, _target.Options), cancellationToken); }
    /// <inheritdoc />
    public IAsyncEnumerable<SpeechChunk> StreamAsync(SpeechRequest request, CancellationToken cancellationToken = default)
        => StreamNativeAsync(AzureSpeechPayload.Bridge(request), cancellationToken);

    private async IAsyncEnumerable<SpeechChunk> SendAsync(AzureSpeechPayload request, [EnumeratorCancellation] CancellationToken token)
    {
        var options = _target.Options; SpeechChunk terminal;
        using (var client = _factory.CreateClient(options.HttpClientName))
        using (var message = _target.Message("speech"))
        {
            message.Content = new ByteArrayContent(request.Bytes); message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            _target.ValidateResponse(response, "speech");
            var type = response.Content.Headers.ContentType;
            if (response.StatusCode != HttpStatusCode.OK || !AzureAudioResponse.Binary(type?.MediaType))
                throw new AzureAudioException((int)response.StatusCode, await HttpBodyReader.ReadAsync(response.Content, options.MaxResponseBytes, token).ConfigureAwait(false), type?.ToString());
            AzureAudioResponse.Format(type?.MediaType, request.Format);
            var expected = response.Content.Headers.ContentLength;
            if (response.Content.Headers.Contains("Content-Length") && expected is null) throw new InvalidDataException("Azure 音频声明长度无效。");
            if (expected > options.MaxAudioBytes) throw new InvalidDataException("Azure 音频声明长度超过上限。");
            var metadata = AzureAudioResponse.Metadata(response, request, options); long count = 0;
            await using (var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            {
                var buffer = new byte[options.ReadBufferSize]; int read;
                while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (read > options.MaxAudioBytes - count || expected is { } declared && read > declared - count) throw new InvalidDataException("Azure 音频超过显式上限或声明长度。");
                    count += read; yield return new(buffer.AsMemory(0, read), false, AzureAudioResponse.Frame(metadata, count, false), Id);
                }
                if (count == 0 || expected is { } length && count != length) throw new InvalidDataException("Azure 音频为空或没有完整接收。");
                terminal = new(ReadOnlyMemory<byte>.Empty, true, AzureAudioResponse.Frame(metadata, count, true), Id);
            }
        }
        token.ThrowIfCancellationRequested(); yield return terminal;
    }
}
