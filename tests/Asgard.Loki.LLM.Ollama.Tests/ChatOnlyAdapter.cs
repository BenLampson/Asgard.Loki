using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Ollama.Tests;

/// <summary>不实现 generate 的原生 Chat 适配器，验证新协议声明不能绕过接口检查。</summary>
internal sealed class ChatOnlyAdapter : IOllamaChatAdapter
{
    /// <inheritdoc />
    public string Id => "ollama";
    /// <inheritdoc />
    public Task<OllamaChatResponse> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    /// <inheritdoc />
    public IAsyncEnumerable<OllamaChatEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
