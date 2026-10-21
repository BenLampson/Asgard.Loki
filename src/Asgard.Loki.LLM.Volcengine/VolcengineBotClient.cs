using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Volcengine.Internal;

namespace Asgard.Loki.LLM.Volcengine;

/// <summary>固定已发布应用的一次原生 Bot Chat 调用；无模型调度、应用管理或本地工具/插件循环。</summary>
public sealed class VolcengineBotClient
{
    private readonly VolcengineBotOptions _options;
    private readonly string _key;
    private readonly ArkHttpTransport _transport;
    /// <summary>快照完整服务/Bot/凭据与本地策略，借用 HTTP 工厂，Key 不进入公开选项或元数据。</summary>
    public VolcengineBotClient(IHttpClientFactory factory, VolcengineBotOptions options, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(options.BaseAddress); ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var root = options.BaseAddress;
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0) throw new ArgumentException("Bot API 根必须是无凭据、查询或片段的 HTTP(S) 绝对地址。");
        foreach (var value in new[] { options.BotId, options.TargetId, options.CredentialId, options.Version, options.HttpClientName, apiKey }) { ArgumentException.ThrowIfNullOrWhiteSpace(value); ArkJson.Text(value); }
        if (apiKey.Any(c => c is < '!' or > '~')) throw new ArgumentException("Bot API Key 必须为无空白/控制字符的 ASCII 内容。");
        _options = options with { BaseAddress = new(root.AbsoluteUri.TrimEnd('/') + "/"), HttpOptions = ArkRequest.Options(options.HttpOptions) }; _key = apiKey;
        Metadata = new(_options.TargetId, _options.BaseAddress, _options.BotId, _options.CredentialId, _options.Version); _transport = new(factory, _options.HttpClientName, _options.HttpOptions);
    }
    /// <summary>固定原生服务/应用身份；返回原文中的 model 可以不同，且可能包含多个远端接入点。</summary>
    public VolcengineBotMetadata Metadata { get; }
    /// <summary>一次非流式原生调用，原始消息历史和全部可选参数由调用者明确提供。</summary>
    public async Task<VolcengineBotResult> CompleteAsync(JsonObject request, CancellationToken cancellationToken = default)
    {
        var message = Request(request, false, cancellationToken); var data = await _transport.PostAsync(message, cancellationToken).ConfigureAwait(false);
        var result = new VolcengineBotResult(data, Metadata); cancellationToken.ThrowIfCancellationRequested(); return result;
    }
    /// <summary>一次流式原生调用，保留所有原始帧及实际 DONE，不合并多模型用量、插件结果或工具参数。</summary>
    public async IAsyncEnumerable<VolcengineBotEvent> StreamAsync(JsonObject request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in _transport.StreamAsync(Request(request, true, cancellationToken), false, cancellationToken).ConfigureAwait(false))
        {
            var done = frame.Data == "[DONE]"; JsonElement? json = done ? null : ArkJson.Parse(Encoding.UTF8.GetBytes(frame.Data));
            yield return new(frame.Data, frame.EventName, frame.EventId, frame.RetryMilliseconds, json, Metadata, done || ArkStreamState.Failure(json!.Value, frame.EventName, false));
        }
    }
    /// <summary>显式原生消息数组，模型/流标志只能属于已选 Bot/调用方法，不补历史、用户或参数默认值。</summary>
    private HttpRequestMessage Request(JsonObject payload, bool stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(payload); ArkJson.Input(payload);
        foreach (var property in payload)
            if (new[] { "model", "stream" }.Any(x => x.Equals(property.Key, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Bot 原生 model/stream 由固定应用和方法拥有，参数不能覆盖或使用别名。");
        if (!payload.TryGetPropertyValue("messages", out var messages) || messages is not JsonArray) throw new ArgumentException("Bot 调用必须提供原生 messages 数组。");
        return ArkRequest.Create(_options.BaseAddress, "chat/completions", _key, payload, _options.BotId, stream, _options.HttpOptions);
    }
}
