using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Internal;

namespace Asgard.Loki.LLM.Google;

/// <summary>实现 Gemini Developer API 原生 generateContent 与 SSE，不承担 Vertex AI 认证。</summary>
public sealed class GoogleLlmProvider : IGeminiAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly string _clientName;

    /// <summary>借用命名 HTTP 工厂，基础地址应包含 v1beta 等版本路径。</summary>
    public GoogleLlmProvider(IHttpClientFactory httpClientFactory, string id = "google",
        string httpClientName = "Asgard.Loki.LLM.Google")
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        _factory = httpClientFactory;
        _clientName = httpClientName;
        Id = id;
    }

    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity => $"{GetType().AssemblyQualifiedName}:{_clientName}";

    /// <inheritdoc />
    public async Task<GeminiContent> GenerateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await NativeLlmHttp.SendAsync(_factory, _clientName, Request(request, context, false), cancellationToken).ConfigureAwait(false), context.Metadata);

    /// <inheritdoc />
    public async IAsyncEnumerable<GeminiContentEvent> StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        HashSet<int> seen = [], finished = [];
        var blockedOrError = false;
        await foreach (var frame in NativeLlmHttp.StreamAsync(_factory, _clientName, Request(request, context, true), cancellationToken).ConfigureAwait(false))
        {
            using var json = JsonDocument.Parse(frame.Data);
            var data = json.RootElement;
            if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Gemini 事件必须是对象。");
            if (blockedOrError) throw new InvalidDataException("Gemini 错误或阻断终态之后仍有数据。");
            blockedOrError |= data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null
                || data.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out var block)
                    && block.ValueKind == JsonValueKind.String && block.GetString() is { Length: > 0 } blockReason && blockReason != "BLOCK_REASON_UNSPECIFIED";
            if (data.TryGetProperty("candidates", out var candidates))
            {
                foreach (var candidate in candidates.EnumerateArray())
                {
                    var index = candidate.TryGetProperty("index", out var i) ? i.GetInt32() : 0;
                    if (index < 0) throw new InvalidDataException("Gemini 候选索引不能为负数。");
                    if (finished.Contains(index) && candidate.TryGetProperty("content", out var content)
                        && content.TryGetProperty("parts", out var parts) && parts.GetArrayLength() != 0)
                        throw new InvalidDataException("Gemini 候选终态之后仍有内容。");
                    seen.Add(index);
                    if (candidate.TryGetProperty("finishReason", out var reason)
                        && reason.ValueKind == JsonValueKind.String && reason.GetString() is { Length: > 0 } finishReason
                        && finishReason != "FINISH_REASON_UNSPECIFIED" && !finished.Add(index))
                        throw new InvalidDataException("Gemini 候选重复结束或改变终态。");
                }
            }
            yield return new GeminiContentEvent(data, context.Metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds);
        }
        if (!blockedOrError && (seen.Count == 0 || !seen.SetEquals(finished)))
            throw new InvalidDataException("Gemini 流在候选结果终态之前断开。");
    }

    /// <summary>模型放在路径，流式开关放在方法及 alt 查询中；正文仅包含 Gemini 原生字段。</summary>
    private static HttpRequestMessage Request(JsonObject payload, LlmAdapterContext context, bool stream)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        var body = payload.DeepClone().AsObject();
        body.Remove("model");
        body.Remove("stream");
        var path = "models/" + Uri.EscapeDataString(context.Target.UpstreamModel)
            + (stream ? ":streamGenerateContent?alt=sse" : ":generateContent");
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(context.Target.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"), path));
        request.Headers.Add("x-goog-api-key", context.Credential.ApiKey);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }
}
