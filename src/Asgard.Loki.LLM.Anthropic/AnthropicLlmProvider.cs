using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Internal;

namespace Asgard.Loki.LLM.Anthropic;

/// <summary>实现 Claude Messages 原生 HTTP/JSON 与 SSE，不改写消息或推理内容。</summary>
public sealed class AnthropicLlmProvider : IClaudeMessagesAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly string _clientName;
    private readonly string _apiVersion;

    /// <summary>借用 HTTP 工厂，API 版本及 Beta 默认头由调用者明确配置。</summary>
    public AnthropicLlmProvider(IHttpClientFactory httpClientFactory, string id = "anthropic",
        string httpClientName = "Asgard.Loki.LLM.Anthropic", string apiVersion = "2023-06-01")
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiVersion);
        _factory = httpClientFactory;
        _clientName = httpClientName;
        _apiVersion = apiVersion;
        Id = id;
    }

    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity => $"{GetType().AssemblyQualifiedName}:{_apiVersion}:{_clientName}";

    /// <inheritdoc />
    public async Task<ClaudeMessage> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await NativeLlmHttp.SendAsync(_factory, _clientName, Request(request, context), cancellationToken).ConfigureAwait(false), context.Metadata);

    /// <inheritdoc />
    public async IAsyncEnumerable<ClaudeMessageEvent> StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var terminal = false;
        await foreach (var frame in NativeLlmHttp.StreamAsync(_factory, _clientName, Request(request, context), cancellationToken).ConfigureAwait(false))
        {
            using var json = JsonDocument.Parse(frame.Data);
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Claude 事件必须是对象。");
            var type = json.RootElement.TryGetProperty("type", out var value) ? value.GetString() : frame.EventName;
            if (terminal) throw new InvalidDataException("Claude 终态之后仍有数据。");
            terminal = type is "message_stop" or "error";
            yield return new ClaudeMessageEvent(json.RootElement, context.Metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds);
        }
        if (!terminal) throw new InvalidDataException("Claude 流缺失 message_stop 或错误终态。");
    }

    /// <summary>在目标基础路径后拼接 messages，认证仅存在本次请求头。</summary>
    private HttpRequestMessage Request(JsonObject payload, LlmAdapterContext context)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(context.Target.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"), "messages"));
        request.Headers.Add("x-api-key", context.Credential.ApiKey);
        request.Headers.Add("anthropic-version", _apiVersion);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }
}
