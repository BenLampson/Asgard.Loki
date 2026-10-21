using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Internal;

namespace Asgard.Loki.LLM.Ollama;

/// <summary>generate 保留原生 prompt/context，独立状态只借用同包内的纯 HTTP/JSON 读取工具。</summary>
public sealed partial class OllamaLlmProvider
{
    /// <inheritdoc />
    public async Task<OllamaGenerateResponse> GenerateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); OllamaGenerateResponse result;
        using (var message = Request(request, context, false, "generate"))
        using (var client = _factory.CreateClient(_clientName))
        {
            Authentication(client, context);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) await ErrorAsync(response, cancellationToken).ConfigureAwait(false);
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not null && !IsJson(media)) throw new InvalidDataException("完整 Ollama generate 调用需要单个原生 JSON。");
            var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bytes = await OllamaJson.ReadAsync(source, _options.MaximumResponseBytes, cancellationToken, response.Content.Headers.ContentLength).ConfigureAwait(false);
            var data = OllamaJson.Parse(bytes);
            if (OllamaJson.IsError(data)) throw new LlmUpstreamException("Ollama generate 返回原生错误。", response.StatusCode, OllamaJson.Text(bytes));
            var state = new OllamaGenerateState(); state.Push(data); state.Complete(); result = new(data, context.Metadata);
        }
        cancellationToken.ThrowIfCancellationRequested(); return result;
    }
    /// <summary>显式接口区分 Chat 与 generate 的原生事件类型。</summary>
    async IAsyncEnumerable<OllamaGenerateEvent> IOllamaGenerateAdapter.StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); OllamaGenerateEvent? terminal = null; var state = new OllamaGenerateState();
        using (var message = Request(request, context, true, "generate"))
        using (var client = _factory.CreateClient(_clientName))
        {
            Authentication(client, context);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) await ErrorAsync(response, cancellationToken).ConfigureAwait(false);
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not null && !IsJson(media) && !string.Equals(media, "application/x-ndjson", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(media, "application/ndjson", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Ollama generate 流需要原生 NDJSON 或单个 JSON 终态。");
            var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            if (media is not null && IsJson(media))
            {
                var data = OllamaJson.Parse(await OllamaJson.ReadAsync(source, _options.MaximumStreamRecordBytes, cancellationToken, response.Content.Headers.ContentLength).ConfigureAwait(false));
                if (!state.Push(data)) throw new InvalidDataException("Ollama generate JSON 缺少原生终态。");
                terminal = new(data, context.Metadata);
            }
            else
                await foreach (var data in OllamaNdjson.ReadAsync(source, _options.MaximumStreamRecordBytes, cancellationToken, response.Content.Headers.ContentLength).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested(); var last = state.Push(data); var item = new OllamaGenerateEvent(data, context.Metadata);
                    if (last) terminal = item; else yield return item;
                }
            state.Complete();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (terminal is null) throw new InvalidDataException("Ollama generate 流缺少完整终态。");
        yield return terminal;
    }
}
