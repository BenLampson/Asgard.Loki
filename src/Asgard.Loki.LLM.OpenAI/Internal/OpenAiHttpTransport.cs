using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.LLM.OpenAI.Internal;

/// <summary>本供应商内部原生 Chat/Responses 传输；认证来源和目标绑定通过调用级校验进入，不调度业务。</summary>
internal static class OpenAiHttpTransport
{
    /// <summary>默认 OpenAI 契约不变；明确账户调用可以复查绑定并保留完整原始错误字节/媒体。</summary>
    internal static async Task<JsonElement> PostAsync(HttpRequestMessage message, IHttpClientFactory factory, string clientName, OpenAiHttpOptions options,
        CancellationToken token, Action<HttpClient>? beforeSend = null, Action<HttpResponseMessage?>? verify = null, bool rawErrors = false)
    {
        JsonElement data; HttpResponseMessage? actualResponse = null;
        using (var request = message)
        {
            token.ThrowIfCancellationRequested(); verify?.Invoke(null);
            using (var client = factory.CreateClient(clientName))
            {
                token.ThrowIfCancellationRequested(); beforeSend?.Invoke(client);
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    actualResponse = response; verify?.Invoke(response); if (rawErrors) Length(response, options.MaximumBodyBytes);
                    await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    var bytes = await OpenAiHttpJson.ReadAsync(source, options.MaximumBodyBytes, response.Content.Headers.ContentLength, token).ConfigureAwait(false);
                    verify?.Invoke(response);
                    if (!response.IsSuccessStatusCode) throw Error("OpenAI HTTP 调用失败。", response, bytes, rawErrors);
                    data = OpenAiHttpJson.Parse(bytes); if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("OpenAI 完整结果必须是 JSON 对象。");
                    if (OpenAiHttpJson.IsError(data)) throw Error("OpenAI 返回原生错误。", response, bytes, rawErrors);
                    var media = response.Content.Headers.ContentType?.MediaType;
                    if (media is not null && !string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase) && !media.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("OpenAI 完整调用没有返回 JSON。");
                    if (rawErrors) Charset(response);
                }
            }
        }
        token.ThrowIfCancellationRequested(); verify?.Invoke(actualResponse); return data;
    }
    /// <summary>复用原生状态和有界终态尾流，完整 EOF/释放/最终取消前不交付终态。</summary>
    internal static async IAsyncEnumerable<SseFrame> StreamAsync(HttpRequestMessage message, IHttpClientFactory factory, string clientName, OpenAiHttpOptions options,
        bool responses, [EnumeratorCancellation] CancellationToken token, Action<HttpClient>? beforeSend = null, Action<HttpResponseMessage?>? verify = null, bool rawErrors = false)
    {
        var pending = new List<SseFrame>(); var state = new OpenAiHttpStreamState(responses); HttpResponseMessage? actualResponse = null;
        using (var request = message)
        {
            token.ThrowIfCancellationRequested(); verify?.Invoke(null);
            using (var client = factory.CreateClient(clientName))
            {
                token.ThrowIfCancellationRequested(); beforeSend?.Invoke(client); if (!request.Headers.Accept.Any(x => x.MediaType == "text/event-stream")) request.Headers.Accept.Add(new("text/event-stream"));
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    actualResponse = response; verify?.Invoke(response); if (rawErrors) Length(response, options.MaximumBodyBytes);
                    await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode || !string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                    {
                        var bytes = await OpenAiHttpJson.ReadAsync(source, options.MaximumBodyBytes, response.Content.Headers.ContentLength, token).ConfigureAwait(false);
                        throw Error("OpenAI 流式调用失败或没有返回 SSE。", response, bytes, rawErrors);
                    }
                    if (rawErrors) Charset(response);
                    using var bounded = new OpenAiHttpReadStream(source, options.MaximumBodyBytes, response.Content.Headers.ContentLength);
                    await foreach (var frame in SseReader.ReadAsync(bounded, token, strictUtf8: true).ConfigureAwait(false))
                    {
                        token.ThrowIfCancellationRequested(); verify?.Invoke(response);
                        if (frame.Data == "[DONE]") { state.ObserveDone(); continue; }
                        var data = OpenAiHttpJson.Parse(Encoding.UTF8.GetBytes(frame.Data)); if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("OpenAI SSE 必须是原生 JSON 对象。");
                        if (state.Finishing && OpenAiHttpStreamState.IsFailure(data, frame.EventName)) throw Error("OpenAI SSE 在可能终态之后返回错误。", response, Encoding.UTF8.GetBytes(frame.Data), rawErrors);
                        state.Observe(data, frame.EventName);
                        if (state.Finishing)
                        { if (pending.Count >= options.MaximumBufferedTerminalEvents) throw new InvalidDataException("OpenAI SSE 终态尾部暂存数超限。"); pending.Add(frame); }
                        else yield return frame;
                    }
                    state.Complete();
                }
            }
        }
        token.ThrowIfCancellationRequested(); verify?.Invoke(actualResponse);
        foreach (var frame in pending) { token.ThrowIfCancellationRequested(); yield return frame; }
    }
    /// <summary>保持既有默认文本错误；显式原生账户模式另外保留任意错误字节和原始媒体。</summary>
    private static LlmUpstreamException Error(string text, HttpResponseMessage response, byte[] body, bool raw)
        => raw ? new(text, response.StatusCode, body, ContentType(response)) : new(text, response.StatusCode, OpenAiHttpJson.Decode(body));
    /// <summary>完整原始媒体值，包括参数和缺省。</summary>
    private static string? ContentType(HttpResponseMessage response) => response.Content.Headers.NonValidated.TryGetValues("Content-Type", out var values) ? string.Join(", ", values) : null;
    /// <summary>显式账户模式不接受 UTF-8 之外的成功 JSON/SSE 字符集。</summary>
    private static void Charset(HttpResponseMessage response)
    {
        var type = ContentType(response);
        if (type is not null && (!MediaTypeHeaderValue.TryParse(type, out var media) || media.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("原生账户响应需要 UTF-8 JSON/SSE 媒体。");
    }
    /// <summary>显式账户模式在读取前拒绝无效/超预算声明。</summary>
    private static void Length(HttpResponseMessage response, int maximum)
    {
        var present = response.Content.Headers.NonValidated.Contains("Content-Length"); var value = response.Content.Headers.ContentLength;
        if (present && value is null || value < 0 || value > maximum) throw new InvalidDataException("原生账户响应声明长度无效或超预算。");
    }
}
