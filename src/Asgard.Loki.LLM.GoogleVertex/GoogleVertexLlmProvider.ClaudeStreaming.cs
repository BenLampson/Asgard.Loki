using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Internal;
using Asgard.Loki.LLM.GoogleVertex.Internal;

namespace Asgard.Loki.LLM.GoogleVertex;

/// <summary>Vertex Claude 原生 streamRawPredict，终态通过 EOF/释放后才按原序交付。</summary>
public sealed partial class GoogleVertexLlmProvider
{
    /// <summary>原生错误事件保留为失败终态；可能成功后到达的错误使本次调用失败而不释放成功。</summary>
    async IAsyncEnumerable<ClaudeMessageEvent> IClaudeMessagesAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var state = new VertexClaudeStreamState(_options.MaximumTrackedClaudeBlocks ?? 128); var pending = new List<(JsonElement Data, SseFrame Frame)>();
        using (var message = await PrepareAsync(request, context, true, cancellationToken, claude: true).ConfigureAwait(false))
        using (var client = _factory.CreateClient(_clientName))
        {
            BeforeSend(client, message, cancellationToken, claude: true);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var bytes = await VertexJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false); throw new LlmUpstreamException("Vertex Claude 原生流式调用失败。", response.StatusCode, VertexJson.Decode(bytes));
            }
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await VertexJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false);
                if (IsError(VertexJson.Parse(bytes))) throw new LlmUpstreamException("Vertex Claude 流式调用返回原生错误。", response.StatusCode, VertexJson.Decode(bytes));
                throw new InvalidDataException("Vertex Claude 流式调用没有返回 SSE。");
            }
            using var bounded = new VertexReadStream(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength);
            await foreach (var frame in SseReader.ReadAsync(bounded, cancellationToken, strictUtf8: true).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested(); var data = VertexJson.Parse(Encoding.UTF8.GetBytes(frame.Data));
                if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Vertex Claude SSE data 必须是原生对象。");
                if (IsError(data))
                {
                    var errorType = data.TryGetProperty("type", out var declared) && declared.ValueKind == JsonValueKind.String ? declared.GetString() : frame.EventName;
                    if (errorType != "error" || state.Finishing) throw new LlmUpstreamException("Vertex Claude SSE 返回原生错误。", response.StatusCode, frame.Data);
                }
                state.Observe(data, frame.EventName);
                if (state.Finishing)
                {
                    if (pending.Count >= _options.MaximumBufferedTerminalEvents) throw new InvalidDataException("Vertex Claude 终态尾部暂存超过配置上限。"); pending.Add((data, frame));
                }
                else yield return new(data, context.Metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds);
            }
            state.Complete();
        }
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var item in pending) { cancellationToken.ThrowIfCancellationRequested(); yield return new(item.Data, context.Metadata, item.Frame.EventName, item.Frame.EventId, item.Frame.RetryMilliseconds); }
    }
}
