using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.Cohere.Internal;

namespace Asgard.Loki.LLM.Cohere;

/// <summary>原生 Cohere v1 Chat JSON/NDJSON 单次调用，不执行工具、自动转换或后台管理。</summary>
public sealed class CohereLlmProvider : ICohereChatAdapter, ILlmCredentialSupport
{
    private readonly IHttpClientFactory _factory;
    private readonly string _httpClientName;
    private readonly CohereLlmOptions _options;
    /// <summary>借用工厂；配置、命名客户端和字节策略参与完整 Session 实现身份。</summary>
    public CohereLlmProvider(IHttpClientFactory factory, string id = "cohere", string httpClientName = "Asgard.Loki.LLM.Cohere", CohereLlmOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        CohereJson.Text(id); CohereJson.Text(httpClientName);
        options = options is null ? new() : options with { };
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumStreamRecordBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumStreamBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumBufferedTailRecords, 0);
        if (options.ClientName is { } name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name); CohereJson.Text(name);
            if (name.Any(char.IsControl)) throw new ArgumentException("Cohere ClientName 不能包含控制换行。");
        }
        _factory = factory; _httpClientName = httpClientName; _options = options; Id = id;
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity => $"{GetType().AssemblyQualifiedName}:cohere-chat-v1-ndjson:{_httpClientName}:{JsonSerializer.Serialize(_options)}";
    /// <inheritdoc />
    public bool SupportsCredential(LlmCredentialKind kind) => kind == LlmCredentialKind.ApiKey;
    /// <inheritdoc />
    public async Task<CohereChatCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var message = CohereRequest.Create(request, context, _options, false); var endpoint = message.RequestUri!.OriginalString;
        HttpResponseMessage? response = null; byte[] bytes; string? type; System.Net.HttpStatusCode status;
        using (message)
        using (var client = _factory.CreateClient(_httpClientName))
        {
            CohereRequest.Defaults(client, _options);
            using (response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                CohereRequest.Verify(message, response, endpoint); status = response.StatusCode; type = ContentType(response);
                if (response.IsSuccessStatusCode) Media(type, false);
                Length(response, _options.MaximumResponseBytes);
                bytes = await HttpBodyReader.ReadAsync(response.Content, _options.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
                CohereRequest.Verify(message, response, endpoint);
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); CohereRequest.Verify(message, response, endpoint);
        if ((int)status is < 200 or >= 300) throw new LlmUpstreamException("Cohere 原生 HTTP 调用失败。", status, bytes, type);
        var data = CohereJson.Parse(bytes); NativeError(data, status, bytes, type); CohereStreamState.Completion(data);
        var result = new CohereChatCompletion(data, context.Metadata); cancellationToken.ThrowIfCancellationRequested(); return result;
    }
    /// <inheritdoc />
    public async IAsyncEnumerable<CohereChatEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var message = CohereRequest.Create(request, context, _options, true); var endpoint = message.RequestUri!.OriginalString;
        HttpResponseMessage? response = null; List<CohereChatEvent> tail = []; var state = new CohereStreamState();
        using (message)
        using (var client = _factory.CreateClient(_httpClientName))
        {
            CohereRequest.Defaults(client, _options);
            using (response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                CohereRequest.Verify(message, response, endpoint); var type = ContentType(response);
                if (!response.IsSuccessStatusCode)
                {
                    Length(response, _options.MaximumResponseBytes);
                    var error = await HttpBodyReader.ReadAsync(response.Content, _options.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
                    throw new LlmUpstreamException("Cohere 原生流 HTTP 调用失败。", response.StatusCode, error, type);
                }
                Media(type, true); var expected = Length(response, _options.MaximumStreamBytes);
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                {
                    await foreach (var line in NdjsonReader.ReadAsync(source, _options.MaximumStreamRecordBytes, _options.MaximumStreamBytes, cancellationToken, expected).ConfigureAwait(false))
                    {
                        cancellationToken.ThrowIfCancellationRequested(); CohereRequest.Verify(message, response, endpoint);
                        if (Blank(line)) continue;
                        var data = CohereJson.Parse(line); NativeError(data, response.StatusCode, line, type); state.Push(data); var item = new CohereChatEvent(data, context.Metadata);
                        if (state.Ended)
                        {
                            if (tail.Count >= _options.MaximumBufferedTailRecords) throw new InvalidDataException("Cohere 终态尾部记录超过配置上限。");
                            tail.Add(item);
                        }
                        else { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
                    }
                    state.Complete();
                }
                CohereRequest.Verify(message, response, endpoint);
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); CohereRequest.Verify(message, response, endpoint);
        foreach (var item in tail) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        cancellationToken.ThrowIfCancellationRequested();
    }
    /// <summary>原始媒体头保留参数，不将错误正文作为 JSON 有损重编码。</summary>
    private static string? ContentType(HttpResponseMessage response) => response.Content.Headers.NonValidated.TryGetValues("Content-Type", out var values) ? string.Join(", ", values) : null;
    /// <summary>完整 JSON 和默认 NDJSON 不协商 SSE；错误正文仍按原始字节交付。</summary>
    private static void Media(string? type, bool stream)
    {
        if (type is null) return;
        if (!MediaTypeHeaderValue.TryParse(type, out var media) || media.MediaType is not { } value
            || !(value.Equals("application/json", StringComparison.OrdinalIgnoreCase) || value.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
                || stream && (value.Equals("application/x-ndjson", StringComparison.OrdinalIgnoreCase) || value.Equals("application/ndjson", StringComparison.OrdinalIgnoreCase)))
            || media.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cohere 原生结果必须为 UTF-8 JSON/NDJSON，不支持 SSE 协商。");
    }
    /// <summary>无效、负数或超预算长度声明在正文读取前失败。</summary>
    private static long? Length(HttpResponseMessage response, long maximum)
    {
        var declared = response.Content.Headers.NonValidated.Contains("Content-Length"); var length = response.Content.Headers.ContentLength;
        if (declared && length is null || length < 0 || length > maximum) throw new InvalidDataException("Cohere Content-Length 无效或超过上限。");
        return length;
    }
    /// <summary>仅忽略 NDJSON 原生空白分隔行，所有字节仍计入流预算。</summary>
    private static bool Blank(ReadOnlyMemory<byte> line)
    { foreach (var value in line.Span) if (value is not (32 or 9 or 13)) return false; return true; }
    /// <summary>明确的原生根 error 封装保留实际状态及原始字节，嵌套业务错误字段不参与判断。</summary>
    private static void NativeError(JsonElement data, System.Net.HttpStatusCode status, ReadOnlyMemory<byte> bytes, string? type)
    { if (CohereJson.Error(data)) throw new LlmUpstreamException("Cohere 返回原生错误。", status, bytes, type); }
}
