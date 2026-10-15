using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Audio.Volcengine;

/// <summary>将火山 V3 HTTP 单向流式语音结果转换为统一音频块。</summary>
public sealed class VolcengineSpeechAdapter : ISpeechAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly VolcengineSpeechOptions _options;

    /// <summary>校验独立的语音认证配置，不访问供应商网络。</summary>
    public VolcengineSpeechAdapter(IHttpClientFactory httpClientFactory, VolcengineSpeechOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ResourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.HttpClientName);
        ArgumentNullException.ThrowIfNull(options.Endpoint);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme is not ("https" or "http")
            || options.Endpoint.UserInfo.Length != 0 || options.Endpoint.Query.Length != 0 || options.Endpoint.Fragment.Length != 0)
            throw new ArgumentException("音频地址必须是没有凭据、查询参数和片段的 HTTP(S) 绝对地址。", nameof(options));
        var apiKeyMode = !string.IsNullOrWhiteSpace(options.ApiKey);
        if (options.ApiKey is not null) ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        var legacyMode = !string.IsNullOrWhiteSpace(options.AppId) && !string.IsNullOrWhiteSpace(options.AccessKey);
        if (apiKeyMode == legacyMode || apiKeyMode && (options.AppId is not null || options.AccessKey is not null))
            throw new ArgumentException("必须且只能提供语音 API Key，或旧版 AppId 与 AccessKey。", nameof(options));
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    /// <inheritdoc />
    public string Id => _options.Id;

    /// <inheritdoc />
    public async IAsyncEnumerable<SpeechChunk> StreamAsync(SpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var client = _httpClientFactory.CreateClient(_options.HttpClientName);
        using var message = CreateRequest(request);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new SpeechUpstreamException("火山语音 HTTP 调用失败。", (int)response.StatusCode,
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var completed = false;
        // 按顶层 JSON 对象读取，网络分块或换行方式不会影响帧边界。
        await foreach (var frame in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream,
            topLevelValues: true, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (completed) throw new InvalidDataException("语音完成帧之后出现额外内容。");
            if (frame.ValueKind != JsonValueKind.Object || !frame.TryGetProperty("code", out var codeValue)
                || !codeValue.TryGetInt32(out var code)) throw new InvalidDataException("火山语音帧缺少有效业务码。");
            if (code is not (0 or 20000000)) throw new SpeechUpstreamException("火山语音合成失败。", code, frame.GetRawText());
            ReadOnlyMemory<byte> bytes = ReadOnlyMemory<byte>.Empty;
            if (frame.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null)
            {
                if (data.ValueKind != JsonValueKind.String) throw new InvalidDataException("语音数据不是 Base64 字符串。");
                try { bytes = Convert.FromBase64String(data.GetString()!); }
                catch (FormatException error) { throw new InvalidDataException("语音数据的 Base64 编码无效。", error); }
            }
            completed = code == 20000000;
            yield return new SpeechChunk(bytes, completed, frame, Id);
        }
        if (!completed) throw new InvalidDataException("火山语音流未返回合成完成帧。");
    }

    /// <summary>独立映射音频公共字段，保留未识别的供应商参数。</summary>
    private HttpRequestMessage CreateRequest(SpeechRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Voice);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Format);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        ArgumentNullException.ThrowIfNull(request.AudioParameters);
        if (new[] { "text", "speaker", "audio_params" }.Any(request.Parameters.ContainsKey)
            || request.AudioParameters.ContainsKey("format") || request.AudioParameters.ContainsKey("sample_rate"))
            throw new ArgumentException("音频扩展参数不能覆盖公共字段。", nameof(request));
        var parameters = request.Parameters.DeepClone().AsObject();
        var audio = request.AudioParameters.DeepClone().AsObject();
        parameters["text"] = request.Text;
        parameters["speaker"] = request.Voice;
        audio["format"] = request.Format;
        if (request.SampleRate is { } sampleRate)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
            audio["sample_rate"] = sampleRate;
        }
        parameters["audio_params"] = audio;
        var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        message.Headers.Add("X-Api-Resource-Id", _options.ResourceId);
        message.Headers.Add("X-Api-Request-Id", Guid.NewGuid().ToString());
        message.Headers.Add("X-Control-Require-Usage-Tokens-Return", "*");
        if (_options.ApiKey is not null) message.Headers.Add("X-Api-Key", _options.ApiKey);
        else
        {
            message.Headers.Add("X-Api-App-Id", _options.AppId);
            message.Headers.Add("X-Api-Access-Key", _options.AccessKey);
        }
        message.Content = new StringContent(new JsonObject { ["req_params"] = parameters }.ToJsonString(), Encoding.UTF8, "application/json");
        return message;
    }
}
