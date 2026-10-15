using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Internal;

namespace Asgard.Loki.LLM.Volcengine;

/// <summary>实现火山方舟 Chat Completions 和 Responses 的 HTTP/JSON 与 SSE 调用。</summary>
public sealed class VolcengineLlmProvider : IChatCompletionsAdapter, IResponsesAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _httpClientName;

    /// <summary>借用命名 HTTP 工厂；调用者应关闭生成类 POST 请求的自动重试。</summary>
    public VolcengineLlmProvider(IHttpClientFactory httpClientFactory, string id = "volcengine",
        string httpClientName = "Asgard.Loki.LLM.Volcengine")
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        _httpClientFactory = httpClientFactory;
        _httpClientName = httpClientName;
        Id = id;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public async Task<ChatCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await PostAsync("chat/completions", request, context, cancellationToken).ConfigureAwait(false), context.Metadata);

    /// <inheritdoc />
    public async Task<Response> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await PostAsync("responses", request, context, cancellationToken).ConfigureAwait(false), context.Metadata);

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in StreamProtocolAsync("chat/completions", request, context, false, cancellationToken).ConfigureAwait(false))
        {
            using var document = JsonDocument.Parse(item.Data);
            yield return new ChatCompletionEvent(document.RootElement, context.Metadata, item.EventName, item.EventId, item.RetryMilliseconds);
        }
    }

    /// <summary>显式区分两种协议的流式结果类型。</summary>
    async IAsyncEnumerable<ResponseEvent> IResponsesAdapter.StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in StreamProtocolAsync("responses", request, context, true, cancellationToken).ConfigureAwait(false))
        {
            using var document = JsonDocument.Parse(item.Data);
            yield return new ResponseEvent(document.RootElement, context.Metadata, item.EventName, item.EventId, item.RetryMilliseconds);
        }
    }

    /// <summary>执行一次 JSON 调用，不自动重试。</summary>
    private async Task<JsonElement> PostAsync(string path, JsonObject payload, LlmAdapterContext context, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(_httpClientName);
        using var request = Request(path, payload, context);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    /// <summary>完整读取 SSE，缺失协议终态时明确报告中断。</summary>
    private async IAsyncEnumerable<SseFrame> StreamProtocolAsync(string path, JsonObject payload,
        LlmAdapterContext context, bool responses, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(_httpClientName);
        using var request = Request(path, payload, context);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw new LlmUpstreamException("流式调用没有返回 SSE 内容。", response.StatusCode,
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var terminal = false;
        await foreach (var item in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (item.Data == "[DONE]")
            {
                if (!responses) terminal = true;
                break;
            }

            using var document = JsonDocument.Parse(item.Data);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("流式事件不是 JSON 对象。");
            if (responses)
            {
                var type = document.RootElement.TryGetProperty("type", out var value) ? value.GetString() : item.EventName;
                terminal |= type is "response.completed" or "response.failed" or "response.incomplete" or "error";
            }
            else if (document.RootElement.TryGetProperty("error", out _)) terminal = true;
            yield return item;
        }

        if (!terminal) throw new InvalidDataException("上游流在协议终态之前断开；不会自动重试生成请求。");
    }

    /// <summary>地址、认证和正文均从已绑定的目标构造，不修改 HTTP 工厂默认头。</summary>
    private static HttpRequestMessage Request(string path, JsonObject payload, LlmAdapterContext context)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        var baseAddress = context.Target.BaseAddress.AbsoluteUri.TrimEnd('/') + "/";
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseAddress), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.Credential.ApiKey);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>保留上游错误正文，便于调用方按统一异常处理。</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new LlmUpstreamException("火山方舟调用失败。", response.StatusCode, body);
    }
}
