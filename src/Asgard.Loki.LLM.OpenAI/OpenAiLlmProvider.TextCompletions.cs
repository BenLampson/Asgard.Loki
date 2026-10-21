using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.OpenAI.Internal;

namespace Asgard.Loki.LLM.OpenAI;

/// <summary>显式配置的兼容原生文本补全，沿用完整目标；不改为 Chat 或补写 FIM 模板。</summary>
public sealed partial class OpenAiLlmProvider
{
    /// <inheritdoc />
    async Task<TextCompletion> ITextCompletionsAdapter.CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var call = new OpenAiTextCompletionCall(request, context, false, _httpOptions.MaximumTextCompletionRequestBytes ?? 32 * 1024 * 1024, _httpOptions.Transport);
        var data = await OpenAiHttpTransport.PostAsync(call.Request, _httpClientFactory, _httpClientName, _httpOptions, cancellationToken, call.BeforeSend, call.Verify, rawErrors: true).ConfigureAwait(false);
        OpenAiTextCompletionJson.Complete(data); cancellationToken.ThrowIfCancellationRequested(); return new(data, context.Metadata);
    }
    /// <inheritdoc />
    async IAsyncEnumerable<TextCompletionEvent> ITextCompletionsAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var call = new OpenAiTextCompletionCall(request, context, true, _httpOptions.MaximumTextCompletionRequestBytes ?? 32 * 1024 * 1024, _httpOptions.Transport);
        var state = new OpenAiTextCompletionStreamState(_httpOptions.MaximumTrackedTextChoices ?? 128);
        await foreach (var frame in OpenAiHttpTransport.StreamAsync(call.Request, _httpClientFactory, _httpClientName, _httpOptions, false, cancellationToken, call.BeforeSend, call.Verify, rawErrors: true, text: state).ConfigureAwait(false))
        { cancellationToken.ThrowIfCancellationRequested(); yield return new(OpenAiHttpJson.Parse(Encoding.UTF8.GetBytes(frame.Data)), context.Metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds); }
    }
}
