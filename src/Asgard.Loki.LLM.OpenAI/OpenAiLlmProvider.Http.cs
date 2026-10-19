using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Internal;
using Asgard.Loki.LLM.OpenAI.Internal;

namespace Asgard.Loki.LLM.OpenAI;

/// <summary>单次 HTTP/JSON 或 SSE 传输；只有完整 EOF/长度/释放/最终取消均成功后才公开完成。</summary>
public sealed partial class OpenAiLlmProvider
{
    /// <summary>响应字节有界并保留原始错误，不根据模型供应商改写请求或重试。</summary>
    private async Task<JsonElement> PostAsync(string path, JsonObject payload, LlmAdapterContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); JsonElement data;
        using (var request = Request(path, payload, context))
        using (var client = _httpClientFactory.CreateClient(_httpClientName))
        {
            token.ThrowIfCancellationRequested(); using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var bytes = await OpenAiHttpJson.ReadAsync(source, _httpOptions.MaximumBodyBytes, response.Content.Headers.ContentLength, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new LlmUpstreamException("OpenAI HTTP 调用失败。", response.StatusCode, OpenAiHttpJson.Decode(bytes));
            data = OpenAiHttpJson.Parse(bytes); if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("OpenAI 完整结果必须是 JSON 对象。");
            if (OpenAiHttpJson.IsError(data)) throw new LlmUpstreamException("OpenAI 返回原生错误。", response.StatusCode, OpenAiHttpJson.Decode(bytes));
            var media = response.Content.Headers.ContentType?.MediaType; if (media is not null && !string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase) && !media.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("OpenAI 完整调用没有返回 JSON。");
        }
        token.ThrowIfCancellationRequested(); return data;
    }
    /// <summary>首个可能终态起有界暂存；DONE 后继续读至 EOF，避免忽略迟到错误或截断尾部。</summary>
    private async IAsyncEnumerable<SseFrame> StreamProtocolAsync(string path, JsonObject payload, LlmAdapterContext context, bool responses, [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var pending = new List<SseFrame>(); var state = new OpenAiHttpStreamState(responses);
        using (var request = Request(path, payload, context))
        using (var client = _httpClientFactory.CreateClient(_httpClientName))
        {
            token.ThrowIfCancellationRequested(); request.Headers.Accept.Add(new("text/event-stream")); using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || !string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await OpenAiHttpJson.ReadAsync(source, _httpOptions.MaximumBodyBytes, response.Content.Headers.ContentLength, token).ConfigureAwait(false); throw new LlmUpstreamException("OpenAI 流式调用失败或没有返回 SSE。", response.StatusCode, OpenAiHttpJson.Decode(bytes));
            }
            using var bounded = new OpenAiHttpReadStream(source, _httpOptions.MaximumBodyBytes, response.Content.Headers.ContentLength);
            await foreach (var frame in SseReader.ReadAsync(bounded, token, strictUtf8: true).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested(); if (frame.Data == "[DONE]") { state.ObserveDone(); continue; }
                var data = OpenAiHttpJson.Parse(Encoding.UTF8.GetBytes(frame.Data)); if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("OpenAI SSE 必须是原生 JSON 对象。");
                if (state.Finishing && OpenAiHttpStreamState.IsFailure(data, frame.EventName)) throw new LlmUpstreamException("OpenAI SSE 在可能终态之后返回错误。", response.StatusCode, frame.Data);
                state.Observe(data, frame.EventName);
                if (state.Finishing) { if (pending.Count >= _httpOptions.MaximumBufferedTerminalEvents) throw new InvalidDataException("OpenAI SSE 终态尾部暂存数超限。"); pending.Add(frame); }
                else yield return frame;
            }
            state.Complete();
        }
        token.ThrowIfCancellationRequested(); foreach (var frame in pending) { token.ThrowIfCancellationRequested(); yield return frame; }
    }
}
