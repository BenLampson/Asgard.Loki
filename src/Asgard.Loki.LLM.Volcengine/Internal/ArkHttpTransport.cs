using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.LLM.Volcengine.Internal;

/// <summary>供应商内原生 HTTP 传输；公共基础设施只提供有界字节/帧，不共享能力业务状态。</summary>
internal sealed class ArkHttpTransport(IHttpClientFactory factory, string clientName, VolcengineHttpOptions options)
{
    /// <summary>正常 EOF、完整长度、全部拥有资源关闭和最终取消之后才交付原始 JSON。</summary>
    internal async Task<JsonElement> PostAsync(HttpRequestMessage message, CancellationToken token)
    {
        var endpoint = message.RequestUri!.OriginalString; var auth = message.Headers.Authorization!.ToString(); var content = message.Content!;
        HttpResponseMessage? response = null; byte[] bytes; HttpStatusCode status; string? type;
        using (message)
        {
            token.ThrowIfCancellationRequested();
            using (var client = factory.CreateClient(clientName))
            {
                Defaults(client); token.ThrowIfCancellationRequested(); Verify(message, null, endpoint, auth, content);
                using (response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    Verify(message, response, endpoint, auth, content); type = Media(response); status = response.StatusCode; Length(response);
                    bytes = await HttpBodyReader.ReadAsync(response.Content, options.MaximumBodyBytes, token).ConfigureAwait(false); Verify(message, response, endpoint, auth, content);
                }
            }
        }
        token.ThrowIfCancellationRequested(); Verify(message, response, endpoint, auth, content);
        if ((int)status is < 200 or >= 300) throw new LlmUpstreamException("方舟原生 HTTP 调用失败。", status, bytes, type);
        var data = ArkJson.Parse(bytes); if (ArkJson.Error(data)) throw new LlmUpstreamException("方舟返回原生错误。", status, bytes, type);
        SuccessMedia(type, false); if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("方舟完整结果必须是原生 JSON 对象。"); return data;
    }
    /// <summary>真实终态/错误起有界缓冲，DONE 后继续检查 EOF 和晚到错误，不截断或重试。</summary>
    internal async IAsyncEnumerable<SseFrame> StreamAsync(HttpRequestMessage message, bool responses, [EnumeratorCancellation] CancellationToken token)
    {
        var endpoint = message.RequestUri!.OriginalString; var auth = message.Headers.Authorization!.ToString(); var content = message.Content!;
        var state = new ArkStreamState(responses, options.MaximumTrackedChoices); List<SseFrame> pending = []; HttpResponseMessage? response = null;
        using (message)
        {
            token.ThrowIfCancellationRequested();
            using (var client = factory.CreateClient(clientName))
            {
                Defaults(client); token.ThrowIfCancellationRequested(); Verify(message, null, endpoint, auth, content);
                using (response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    Verify(message, response, endpoint, auth, content); var type = Media(response); var expected = Length(response);
                    if (!response.IsSuccessStatusCode || !MediaTypeHeaderValue.TryParse(type, out var media) || !string.Equals(media.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                    { var bytes = await HttpBodyReader.ReadAsync(response.Content, options.MaximumBodyBytes, token).ConfigureAwait(false); throw new LlmUpstreamException("方舟原生流 HTTP 失败或未返回 SSE。", response.StatusCode, bytes, type); }
                    SuccessMedia(type, true);
                    await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                    {
                        await foreach (var frame in SseReader.ReadBoundedAsync(source, options.MaximumBodyBytes, token, strictUtf8: true, expectedLength: expected).ConfigureAwait(false))
                        {
                            token.ThrowIfCancellationRequested(); Verify(message, response, endpoint, auth, content);
                            if (frame.Data == "[DONE]") state.Done();
                            else
                            {
                                var data = ArkJson.Parse(Encoding.UTF8.GetBytes(frame.Data)); if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("方舟原生 SSE 需要 JSON 对象或实际 DONE。");
                                if (state.Finishing && ArkStreamState.Failure(data, frame.EventName, responses)) throw new LlmUpstreamException("方舟在可能终态后返回原生错误。", response.StatusCode, Encoding.UTF8.GetBytes(frame.Data), type);
                                state.Observe(data, frame.EventName);
                            }
                            if (state.Finishing) { if (pending.Count >= options.MaximumBufferedTerminalEvents) throw new InvalidDataException("方舟原生终态尾部帧数超限。"); pending.Add(frame); }
                            else yield return frame;
                        }
                        state.Complete();
                    }
                    Verify(message, response, endpoint, auth, content);
                }
            }
        }
        token.ThrowIfCancellationRequested(); Verify(message, response, endpoint, auth, content);
        foreach (var frame in pending) { token.ThrowIfCancellationRequested(); yield return frame; }
    }
    /// <summary>另一种默认认证或签名不能成为未绑定的身份。</summary>
    private static void Defaults(HttpClient client)
    { if (new[] { "api-key", "x-api-key", "x-goog-api-key", "x-amz-date", "x-amz-security-token", "x-amz-content-sha256" }.Any(client.DefaultRequestHeaders.Contains)) throw new InvalidOperationException("方舟命名客户端含未选认证或签名头。"); }
    /// <summary>派发/读取/关闭边界维持原始方法、完整地址和选中认证，不接受重定向或内容替换。</summary>
    private static void Verify(HttpRequestMessage message, HttpResponseMessage? response, string endpoint, string auth, HttpContent content)
    {
        if (message.Method != HttpMethod.Post || message.RequestUri?.OriginalString != endpoint || response?.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri?.OriginalString != endpoint)
            || message.Headers.Authorization?.ToString() != auth || message.Headers.Host != message.RequestUri.Authority || !ReferenceEquals(message.Content, content) || content.Headers.ContentType?.ToString() != "application/json; charset=utf-8")
            throw new InvalidDataException("方舟原生实际方法、完整目标、认证或正文发生变化。");
    }
    /// <summary>完整原始媒体值与缺省均不改写。</summary>
    private static string? Media(HttpResponseMessage response) => response.Content.Headers.NonValidated.TryGetValues("Content-Type", out var values) ? string.Join(", ", values) : null;
    private long? Length(HttpResponseMessage response)
    {
        var present = response.Content.Headers.NonValidated.Contains("Content-Length"); var value = response.Content.Headers.ContentLength;
        if (present && value is null || value < 0 || value > options.MaximumBodyBytes) throw new InvalidDataException("方舟原始响应声明长度无效或超过预算。"); return value;
    }
    private static void SuccessMedia(string? type, bool stream)
    {
        if (type is null && !stream) return;
        if (!MediaTypeHeaderValue.TryParse(type, out var media) || media.MediaType is not { } value
            || !(stream ? value.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) : value.Equals("application/json", StringComparison.OrdinalIgnoreCase) || value.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
            || media.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("方舟成功结果需要对应 UTF-8 JSON/SSE 媒体。");
    }
}
