using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.GoogleVertex.Internal;

namespace Asgard.Loki.LLM.GoogleVertex;

/// <summary>原生 streamGenerateContent SSE；单次调用保留原始事件和有限终态尾部。</summary>
public sealed partial class GoogleVertexLlmProvider
{
    /// <summary>增量直接交付，首个候选终态/提示拦截起的整个尾部通过 EOF 和释放后按原序交付。</summary>
    public async IAsyncEnumerable<GeminiContentEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var state = new VertexStreamState(_options.MaximumTrackedCandidates); var pending = new List<(JsonElement Data, SseFrame Frame)>();
        using (var message = await PrepareAsync(request, context, true, cancellationToken).ConfigureAwait(false))
        using (var client = _factory.CreateClient(_clientName))
        {
            BeforeSend(client, message, cancellationToken);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var bytes = await VertexJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false);
                throw new LlmUpstreamException("Vertex 原生流式调用失败。", response.StatusCode, VertexJson.Decode(bytes));
            }
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await VertexJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false); var data = VertexJson.Parse(bytes);
                if (IsError(data)) throw new LlmUpstreamException("Vertex 流式调用返回原生错误。", response.StatusCode, VertexJson.Decode(bytes));
                throw new InvalidDataException("Vertex 流式调用没有返回 SSE。");
            }
            using var bounded = new VertexReadStream(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength);
            await foreach (var frame in SseReader.ReadAsync(bounded, cancellationToken, strictUtf8: true).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested(); var data = VertexJson.Parse(Encoding.UTF8.GetBytes(frame.Data));
                if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Vertex SSE data 必须是原生对象。");
                if (IsError(data)) throw new LlmUpstreamException("Vertex SSE 返回原生错误。", response.StatusCode, frame.Data);
                state.Observe(data);
                if (state.Finishing)
                {
                    if (pending.Count >= _options.MaximumBufferedTerminalEvents) throw new InvalidDataException("Vertex SSE 终态尾部暂存超过配置上限。"); pending.Add((data, frame));
                }
                else yield return new(data, context.Metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds);
            }
            state.Complete();
        }
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var item in pending) { cancellationToken.ThrowIfCancellationRequested(); yield return new(item.Data, context.Metadata, item.Frame.EventName, item.Frame.EventId, item.Frame.RetryMilliseconds); }
    }
    /// <summary>只识别根级上游错误，不解释函数参数中的业务 error。</summary>
    private static bool IsError(JsonElement data) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null;
}
