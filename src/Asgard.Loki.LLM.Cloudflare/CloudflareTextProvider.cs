using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.Cloudflare.Internal;

namespace Asgard.Loki.LLM.Cloudflare;

/// <summary>原生 Workers AI ai/run 文本 HTTP JSON/SSE，不猜测兼容协议、执行工具或主持业务流程。</summary>
public sealed class CloudflareTextProvider : IWorkersAiTextAdapter, ILlmCredentialSupport
{
    private readonly IHttpClientFactory _factory;
    private readonly string _httpClientName;
    private readonly CloudflareTextOptions _options;
    /// <summary>借用命名 HTTP 工厂；全部本地字节策略参与完整 Session 实现身份。</summary>
    public CloudflareTextProvider(IHttpClientFactory factory, string id = "cloudflare-text", string httpClientName = "Asgard.Loki.LLM.Cloudflare", CloudflareTextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        CloudflareJson.Text(id); CloudflareJson.Text(httpClientName); options = options is null ? new() : options with { };
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumStreamBytes, 0); _factory = factory; _httpClientName = httpClientName; _options = options; Id = id;
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity => $"{GetType().AssemblyQualifiedName}:workers-native-text-v1:{_httpClientName}:{JsonSerializer.Serialize(_options)}";
    /// <inheritdoc />
    public bool SupportsCredential(LlmCredentialKind kind) => kind == LlmCredentialKind.ApiKey;
    /// <inheritdoc />
    public async Task<WorkersAiTextCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var message = CloudflareTextRequest.Create(request, context, _options, false); var endpoint = message.RequestUri!.OriginalString;
        HttpResponseMessage? response = null; byte[] bytes; string? type; System.Net.HttpStatusCode status;
        using (message)
        using (var client = _factory.CreateClient(_httpClientName))
        {
            CloudflareTextRequest.Defaults(client);
            using (response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                CloudflareTextRequest.Verify(message, response, endpoint); status = response.StatusCode; type = ContentType(response);
                if (response.IsSuccessStatusCode) Media(type, false); Length(response, _options.MaximumResponseBytes);
                bytes = await HttpBodyReader.ReadAsync(response.Content, _options.MaximumResponseBytes, cancellationToken).ConfigureAwait(false); CloudflareTextRequest.Verify(message, response, endpoint);
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); CloudflareTextRequest.Verify(message, response, endpoint);
        if ((int)status is < 200 or >= 300) throw new LlmUpstreamException("Workers AI 原生 HTTP 调用失败。", status, bytes, type);
        var data = CloudflareJson.Parse(bytes); NativeError(data, status, bytes, type); CloudflareTextResponse.Complete(data);
        var result = new WorkersAiTextCompletion(data, context.Metadata); cancellationToken.ThrowIfCancellationRequested(); return result;
    }
    /// <inheritdoc />
    public async IAsyncEnumerable<WorkersAiTextEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var message = CloudflareTextRequest.Create(request, context, _options, true); var endpoint = message.RequestUri!.OriginalString;
        HttpResponseMessage? response = null; WorkersAiTextEvent? terminal = null;
        using (message)
        using (var client = _factory.CreateClient(_httpClientName))
        {
            CloudflareTextRequest.Defaults(client);
            using (response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                CloudflareTextRequest.Verify(message, response, endpoint); var type = ContentType(response);
                if (!response.IsSuccessStatusCode)
                {
                    Length(response, _options.MaximumResponseBytes); var error = await HttpBodyReader.ReadAsync(response.Content, _options.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
                    throw new LlmUpstreamException("Workers AI 原生流 HTTP 调用失败。", response.StatusCode, error, type);
                }
                Media(type, true); var expected = Length(response, _options.MaximumStreamBytes);
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                {
                    await foreach (var frame in SseReader.ReadBoundedAsync(source, _options.MaximumStreamBytes, cancellationToken, strictUtf8: true, expectedLength: expected).ConfigureAwait(false))
                    {
                        cancellationToken.ThrowIfCancellationRequested(); CloudflareTextRequest.Verify(message, response, endpoint);
                        WorkersAiTextEvent item;
                        try { item = new(frame.Data, context.Metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds); }
                        catch (ArgumentException error) { throw new InvalidDataException("Workers AI 原生 SSE 不是对象或实际 DONE 控制帧。", error); }
                        if (item.Json is { } native) NativeError(native, response.StatusCode, Encoding.UTF8.GetBytes(frame.Data), type);
                        if (terminal is not null) throw new InvalidDataException("Workers AI 实际 DONE 之后出现重复终态或迟到数据。");
                        if (item.IsTerminal) { terminal = item; continue; }
                        CloudflareTextResponse.Record(item.Json!.Value);
                        cancellationToken.ThrowIfCancellationRequested(); yield return item;
                    }
                    if (terminal is null) throw new InvalidDataException("Workers AI 原生文本流缺少实际 DONE 控制帧。");
                }
                CloudflareTextRequest.Verify(message, response, endpoint);
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); CloudflareTextRequest.Verify(message, response, endpoint);
        yield return terminal!; cancellationToken.ThrowIfCancellationRequested();
    }
    /// <summary>保留原始媒体头，不有损解码错误字节。</summary>
    private static string? ContentType(HttpResponseMessage response) => response.Content.Headers.NonValidated.TryGetValues("Content-Type", out var values) ? string.Join(", ", values) : null;
    /// <summary>只允许当前明确选择的原生 JSON/SSE 及 UTF-8，不自动切换协议。</summary>
    private static void Media(string? type, bool stream)
    {
        if (type is null) return;
        if (!MediaTypeHeaderValue.TryParse(type, out var media) || media.MediaType is not { } value
            || !(stream ? value.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) : value.Equals("application/json", StringComparison.OrdinalIgnoreCase) || value.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
            || media.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Workers AI 原生调用需要对应的 UTF-8 JSON/SSE 媒体。");
    }
    /// <summary>坏长度和超预算声明在首次读取前失败。</summary>
    private static long? Length(HttpResponseMessage response, long maximum)
    {
        var declared = response.Content.Headers.NonValidated.Contains("Content-Length"); var length = response.Content.Headers.ContentLength;
        if (declared && length is null || length < 0 || length > maximum) throw new InvalidDataException("Workers AI 原生声明长度无效或超过预算。");
        return length;
    }
    /// <summary>显式原生错误保留原始正文/帧数据、实际状态和媒体，不推断嵌套业务 error。</summary>
    private static void NativeError(JsonElement data, System.Net.HttpStatusCode status, ReadOnlyMemory<byte> bytes, string? type)
    { if (CloudflareTextResponse.Error(data)) throw new LlmUpstreamException("Workers AI 返回原生错误。", status, bytes, type); }
}
