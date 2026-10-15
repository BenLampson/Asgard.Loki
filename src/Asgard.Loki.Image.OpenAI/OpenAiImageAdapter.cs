using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Internal;

namespace Asgard.Loki.Image.OpenAI;

/// <summary>实现 OpenAI Images Generations 的完整结果与 SSE 部分图像流。</summary>
public sealed class OpenAiImageAdapter : IImageGenerationAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly OpenAiImageOptions _options;

    /// <summary>校验图像供应商配置，不发起网络请求。</summary>
    public OpenAiImageAdapter(IHttpClientFactory factory, OpenAiImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.HttpClientName);
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("https" or "http")
            || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0)
            throw new ArgumentException("图像基础地址必须是没有凭据、查询参数和片段的 HTTP(S) 绝对地址。", nameof(options));
        _factory = factory;
        _options = options;
    }
    /// <inheritdoc />
    public string Id => _options.Id;

    /// <inheritdoc />
    public async Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        using var client = _factory.CreateClient(_options.HttpClientName);
        using var message = CreateRequest(request, false);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new ImageGenerationResult(document.RootElement, Metadata(request));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ImageGenerationEvent> StreamAsync(ImageGenerationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var client = _factory.CreateClient(_options.HttpClientName);
        using var message = CreateRequest(request, true);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw new ImageUpstreamException("图像流没有返回 SSE。", (int)response.StatusCode,
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var terminal = false;
        await foreach (var frame in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (frame.Data == "[DONE]") break;
            using var document = JsonDocument.Parse(frame.Data);
            var item = new ImageGenerationEvent(document.RootElement, Metadata(request), frame.EventName, frame.EventId, frame.RetryMilliseconds);
            terminal |= item.Type is "image_generation.completed" or "image_generation.failed" or "error";
            yield return item;
        }
        if (!terminal) throw new InvalidDataException("图像流在生成终态之前断开；不会自动重试。");
    }

    /// <summary>映射真实模型，保留背景、尺寸、质量、数量和未知参数。</summary>
    private HttpRequestMessage CreateRequest(ImageGenerationRequest request, bool stream)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.Parameters.ContainsKey("model") || request.Parameters.ContainsKey("prompt"))
            throw new ArgumentException("图像扩展参数不能覆盖公共字段。", nameof(request));
        var payload = JsonSerializer.SerializeToNode(request)!.AsObject();
        payload["model"] = _options.Model;
        payload["stream"] = stream;
        var uri = new Uri(new Uri(_options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"), "images/generations");
        var message = new HttpRequestMessage(HttpMethod.Post, uri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        if (_options.Organization is not null) message.Headers.Add("OpenAI-Organization", _options.Organization);
        if (_options.Project is not null) message.Headers.Add("OpenAI-Project", _options.Project);
        message.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        return message;
    }
    /// <summary>将公共路由身份与上游模型分开返回。</summary>
    private ImageResultMetadata Metadata(ImageGenerationRequest request) => new(request.Model, _options.Model, Id);
    /// <summary>传播上游状态与错误正文。</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        throw new ImageUpstreamException("OpenAI 图像生成失败。", (int)response.StatusCode,
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }
}
