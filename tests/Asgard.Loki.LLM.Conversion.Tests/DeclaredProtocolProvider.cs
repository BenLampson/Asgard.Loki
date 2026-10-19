using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>物理适配接口与明确声明分离的替身，构造器验证不得发起真实调用。</summary>
internal sealed class DeclaredProtocolProvider(LlmProtocol allowed) : IChatCompletionsAdapter, IBedrockNovaAdapter, IBedrockConverseAdapter, ILlmProtocolSupport
{
    internal int Calls { get; private set; }
    /// <inheritdoc />
    public string Id => "declared";
    /// <inheritdoc />
    public bool SupportsProtocol(LlmProtocol protocol) => protocol != LlmProtocol.None && allowed.HasFlag(protocol);
    /// <inheritdoc />
    public Task<ChatCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default) { Calls++; throw new NotSupportedException(); }
    /// <inheritdoc />
    public Task<BedrockNovaResponse> InvokeAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default) { Calls++; throw new NotSupportedException(); }
    /// <inheritdoc />
    public Task<BedrockConverseResponse> ConverseAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default) { Calls++; throw new NotSupportedException(); }
    /// <inheritdoc />
    async IAsyncEnumerable<ChatCompletionEvent> IChatCompletionsAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken token)
    { await Task.CompletedTask; Calls++; token.ThrowIfCancellationRequested(); yield break; }
    /// <inheritdoc />
    async IAsyncEnumerable<BedrockNovaEvent> IBedrockNovaAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken token)
    { await Task.CompletedTask; Calls++; token.ThrowIfCancellationRequested(); yield break; }
    /// <inheritdoc />
    async IAsyncEnumerable<BedrockConverseEvent> IBedrockConverseAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken token)
    { await Task.CompletedTask; Calls++; token.ThrowIfCancellationRequested(); yield break; }
}
