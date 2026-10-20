using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.LLM.OpenAI.Internal;

/// <summary>本供应商内部共享的原生 Chat/Responses HTTP 传输；不选择模型、认证或调度目标。</summary>
internal static class OpenAiHttpTransport
{
    /// <summary>响应字节有界并保留原始错误，不根据模型供应商改写请求或重试。</summary>
    internal static async Task<JsonElement> PostAsync(HttpRequestMessage message, IHttpClientFactory factory, string clientName, OpenAiHttpOptions options, CancellationToken token)
    {
        JsonElement data;
        using (var request = message)
        {
            token.ThrowIfCancellationRequested();
            using (var client = factory.CreateClient(clientName))
            {
                token.ThrowIfCancellationRequested(); using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var bytes = await OpenAiHttpJson.ReadAsync(source, options.MaximumBodyBytes, response.Content.Headers.ContentLength, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new LlmUpstreamException("OpenAI HTTP 调用失败。", response.StatusCode, OpenAiHttpJson.Decode(bytes));
                data = OpenAiHttpJson.Parse(bytes); if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("OpenAI 完整结果必须是 JSON 对象。");
                if (OpenAiHttpJson.IsError(data)) throw new LlmUpstreamException("OpenAI 返回原生错误。", response.StatusCode, OpenAiHttpJson.Decode(bytes));
                var media = response.Content.Headers.ContentType?.MediaType; if (media is not null && !string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase) && !media.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("OpenAI 完整调用没有返回 JSON。");
        }
        }
        token.ThrowIfCancellationRequested(); return data;
    }
    /// <summary>首个可能终态起有界暂存；DONE 后继续读至 EOF，避免忽略迟到错误或截断尾部。</summary>
    internal static async IAsyncEnumerable<SseFrame> StreamAsync(HttpRequestMessage message, IHttpClientFactory factory, string clientName, OpenAiHttpOptions options, bool responses, [EnumeratorCancellation] CancellationToken token)
    {
        var pending = new List<SseFrame>(); var state = new OpenAiHttpStreamState(responses);
        using (var request = message)
        {
            token.ThrowIfCancellationRequested();
            using (var client = factory.CreateClient(clientName))
            {
                token.ThrowIfCancellationRequested(); request.Headers.Accept.Add(new("text/event-stream")); using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || !string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                    var bytes = await OpenAiHttpJson.ReadAsync(source, options.MaximumBodyBytes, response.Content.Headers.ContentLength, token).ConfigureAwait(false); throw new LlmUpstreamException("OpenAI 流式调用失败或没有返回 SSE。", response.StatusCode, OpenAiHttpJson.Decode(bytes));
                }
                using var bounded = new OpenAiHttpReadStream(source, options.MaximumBodyBytes, response.Content.Headers.ContentLength);
                await foreach (var frame in SseReader.ReadAsync(bounded, token, strictUtf8: true).ConfigureAwait(false))
            {
                    token.ThrowIfCancellationRequested(); if (frame.Data == "[DONE]") { state.ObserveDone(); continue; }
                    var data = OpenAiHttpJson.Parse(Encoding.UTF8.GetBytes(frame.Data)); if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("OpenAI SSE 必须是原生 JSON 对象。");
                    if (state.Finishing && OpenAiHttpStreamState.IsFailure(data, frame.EventName)) throw new LlmUpstreamException("OpenAI SSE 在可能终态之后返回错误。", response.StatusCode, frame.Data);
                    state.Observe(data, frame.EventName);
                    if (state.Finishing) { if (pending.Count >= options.MaximumBufferedTerminalEvents) throw new InvalidDataException("OpenAI SSE 终态尾部暂存数超限。"); pending.Add(frame); }
                    else yield return frame;
                }
                state.Complete();
        }
        }
        token.ThrowIfCancellationRequested(); foreach (var frame in pending) { token.ThrowIfCancellationRequested(); yield return frame; }
    }
}
