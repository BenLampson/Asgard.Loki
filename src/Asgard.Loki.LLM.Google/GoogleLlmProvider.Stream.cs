using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.Google.Internal;

namespace Asgard.Loki.LLM.Google;

/// <summary>原生候选终态起有界暂存，正常 EOF 和全部资源关闭后再交付结束尾部。</summary>
public sealed partial class GoogleLlmProvider
{
    private async IAsyncEnumerable<GeminiContentEvent> StreamCoreAsync(JsonObject payload, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var state = new GoogleStreamState(_options.MaximumTrackedCandidates); List<GeminiContentEvent> tail = [];
        byte[]? rejected = null; HttpStatusCode status = default; string? type = null;
        using (var request = Request(payload, context, true))
        using (var client = _factory.CreateClient(_clientName))
        {
            token.ThrowIfCancellationRequested(); ValidateDefaults(client);
            using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                status = response.StatusCode; type = ContentType(response.Content); var length = Length(response.Content);
                if (!response.IsSuccessStatusCode || !string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                    rejected = await HttpBodyReader.ReadAsync(response.Content, _options.MaximumBodyBytes, token).ConfigureAwait(false);
                else
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    await using var frames = SseReader.ReadBoundedAsync(stream, _options.MaximumBodyBytes, token, strictUtf8: true, expectedLength: length).GetAsyncEnumerator(token);
                    while (true)
                    {
                        bool moved; try { moved = await frames.MoveNextAsync().ConfigureAwait(false); } catch (DecoderFallbackException error) { throw new InvalidDataException("Gemini SSE 不是有效 UTF-8。", error); }
                        if (!moved) break; token.ThrowIfCancellationRequested(); var frame = frames.Current; var original = GoogleHttpJson.Utf8.GetBytes(frame.Data); JsonElement data;
                        try { data = GoogleHttpJson.Parse(original); }
                        catch (Exception error) when (frame.EventName == "error" && error is JsonException or InvalidDataException) { throw new LlmUpstreamException("Gemini 返回原生错误事件。", status, original, type); }
                        if (state.LateFailure(data, frame.EventName)) throw new LlmUpstreamException("Gemini 在可能终态之后返回原生失败。", status, original, type);
                        state.Observe(data, frame.EventName); var item = new GeminiContentEvent(data, context.Metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds);
                        if (state.Finishing) { if (tail.Count >= _options.MaximumBufferedTerminalEvents) throw new InvalidDataException("Gemini 结束尾帧超过预算。"); tail.Add(item); }
                        else yield return item;
                    }
                    state.Complete();
                }
            }
        }
        token.ThrowIfCancellationRequested(); if (rejected is not null) throw new LlmUpstreamException("Gemini 流式调用失败或未返回 SSE。", status, rejected, type);
        foreach (var item in tail) { token.ThrowIfCancellationRequested(); yield return item; }
    }
}
