using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.Perplexity.Internal;

namespace Asgard.Loki.LLM.Perplexity;

/// <summary>原生 Perplexity Responses JSON/SSE 单次调用，不执行工具或管理上游历史。</summary>
public sealed class PerplexityResponsesProvider : IResponsesAdapter, ILlmCredentialSupport, ILlmProtocolSupport
{
    private readonly IHttpClientFactory _factory;
    private readonly string _client;
    private readonly PerplexityResponsesOptions _options;
    /// <summary>借用命名 HTTP 工厂；显式本地策略与客户端名称参与完整 Session 绑定。</summary>
    public PerplexityResponsesProvider(IHttpClientFactory factory, PerplexityResponsesOptions? options = null, string id = "perplexity", string httpClientName = "Asgard.Loki.LLM.Perplexity")
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName); PerplexityJson.Text(id); PerplexityJson.Text(httpClientName);
        _options = options is null ? new() : options with { };
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.MaximumRequestBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.MaximumResponseBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.MaximumStreamBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.MaximumBufferedTerminalEvents, 0);
        _factory = factory; _client = httpClientName; Id = id;
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity => $"{GetType().AssemblyQualifiedName}:perplexity-responses-sse-v1:{_client}:{JsonSerializer.Serialize(_options)}";
    /// <inheritdoc />
    public bool SupportsCredential(LlmCredentialKind kind) => kind == LlmCredentialKind.ApiKey;
    /// <inheritdoc />
    public bool SupportsProtocol(LlmProtocol protocol) => protocol == LlmProtocol.Responses;
    /// <inheritdoc />
    public async Task<Response> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var call = new PerplexityCall(request, context, false, _options.MaximumRequestBytes);
        HttpResponseMessage? response = null; byte[] bytes; string? type; System.Net.HttpStatusCode status;
        using (call)
        using (var client = _factory.CreateClient(_client))
        {
            call.BeforeSend(client);
            using (response = await client.SendAsync(call.Request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                call.Verify(response); status = response.StatusCode; type = ContentType(response); Length(response, _options.MaximumResponseBytes);
                if (response.IsSuccessStatusCode) Media(type, false);
                bytes = await HttpBodyReader.ReadAsync(response.Content, _options.MaximumResponseBytes, cancellationToken).ConfigureAwait(false); call.Verify(response);
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); call.Verify(response);
        if ((int)status is < 200 or >= 300) throw new LlmUpstreamException("Perplexity 原生 HTTP 调用失败。", status, bytes, type);
        var data = PerplexityJson.Parse(bytes); NativeError(data, status, bytes, type); PerplexityStreamState.Response(data);
        cancellationToken.ThrowIfCancellationRequested(); return new(data, context.Metadata);
    }
    /// <inheritdoc />
    public async IAsyncEnumerable<ResponseEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var call = new PerplexityCall(request, context, true, _options.MaximumRequestBytes); var state = new PerplexityStreamState(); List<ResponseEvent> tail = []; HttpResponseMessage? response = null;
        using (call)
        using (var client = _factory.CreateClient(_client))
        {
            call.BeforeSend(client);
            using (response = await client.SendAsync(call.Request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                call.Verify(response); var type = ContentType(response);
                if (!response.IsSuccessStatusCode)
                {
                    Length(response, _options.MaximumResponseBytes); var bytes = await HttpBodyReader.ReadAsync(response.Content, _options.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
                    throw new LlmUpstreamException("Perplexity 原生流 HTTP 调用失败。", response.StatusCode, bytes, type);
                }
                Media(type, true); var expected = Length(response, _options.MaximumStreamBytes);
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                {
                    await foreach (var frame in SseReader.ReadBoundedAsync(source, _options.MaximumStreamBytes, cancellationToken, strictUtf8: true, expectedLength: expected).ConfigureAwait(false))
                    {
                        cancellationToken.ThrowIfCancellationRequested(); call.Verify(response);
                        if (frame.Data == "[DONE]") { state.Done(); continue; }
                        var bytes = PerplexityJson.Utf8.GetBytes(frame.Data); var data = PerplexityJson.Parse(bytes); NativeError(data, response.StatusCode, bytes, type, frame.EventName); state.Push(data, frame.EventName);
                        var item = new ResponseEvent(data, context.Metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds);
                        if (state.Finishing) { if (tail.Count >= _options.MaximumBufferedTerminalEvents) throw new InvalidDataException("Perplexity 终态尾部事件超过上限。"); tail.Add(item); }
                        else { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
                    }
                    state.Complete();
                }
                call.Verify(response);
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); call.Verify(response);
        foreach (var item in tail) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        cancellationToken.ThrowIfCancellationRequested();
    }
    /// <summary>保留原始媒体头，包括错误正文媒体参数。</summary>
    private static string? ContentType(HttpResponseMessage response) => response.Content.Headers.NonValidated.TryGetValues("Content-Type", out var values) ? string.Join(", ", values) : null;
    /// <summary>成功调用明确协商 JSON 或 SSE，字符集只能为 UTF-8。</summary>
    private static void Media(string? type, bool stream)
    {
        if (type is null || !MediaTypeHeaderValue.TryParse(type, out var media) || media.MediaType is not { } value
            || !(stream ? value.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) : value.Equals("application/json", StringComparison.OrdinalIgnoreCase) || value.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
            || media.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Perplexity 成功结果必须使用所选 UTF-8 JSON/SSE 媒体。");
    }
    /// <summary>无效或超预算长度声明在任何正文读取前失败。</summary>
    private static long? Length(HttpResponseMessage response, long maximum)
    {
        var present = response.Content.Headers.NonValidated.Contains("Content-Length"); var length = response.Content.Headers.ContentLength;
        if (present && length is null || length < 0 || length > maximum) throw new InvalidDataException("Perplexity Content-Length 无效或超过预算。");
        return length;
    }
    /// <summary>HTTP 200 内的供应商错误同样保留原始状态、JSON 载荷和媒体，不交付成功尾部。</summary>
    private static void NativeError(JsonElement data, System.Net.HttpStatusCode status, ReadOnlyMemory<byte> bytes, string? type, string? eventName = null)
    { if (PerplexityJson.Failure(data, eventName)) throw new LlmUpstreamException("Perplexity 返回原生错误。", status, bytes, type); }
}
