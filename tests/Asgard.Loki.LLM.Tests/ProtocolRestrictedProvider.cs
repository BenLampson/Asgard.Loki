using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Tests;

/// <summary>物理接口比明确协议声明更宽的替身，不产生网络请求或改变目标。</summary>
internal sealed class ProtocolRestrictedProvider(LlmProtocol allowed) : IChatCompletionsAdapter, IResponsesAdapter, ILlmProtocolSupport
{
    private readonly StubLlmProvider _inner = new();
    internal List<LlmProtocol> Checked { get; } = [];
    internal int Calls => _inner.Calls.Count;
    /// <inheritdoc />
    public string Id => _inner.Id;
    /// <inheritdoc />
    public string BindingIdentity => "restricted-v1-" + (int)allowed;
    /// <inheritdoc />
    public bool SupportsProtocol(LlmProtocol protocol) { Checked.Add(protocol); return allowed.HasFlag(protocol); }
    /// <inheritdoc />
    public Task<ChatCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => _inner.CompleteAsync(request, context, cancellationToken);
    /// <inheritdoc />
    public Task<Response> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => _inner.CreateAsync(request, context, cancellationToken);
    /// <inheritdoc />
    async IAsyncEnumerable<ChatCompletionEvent> IChatCompletionsAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken token)
    { await foreach (var item in ((IChatCompletionsAdapter)_inner).StreamAsync(request, context, token)) yield return item; }
    /// <inheritdoc />
    async IAsyncEnumerable<ResponseEvent> IResponsesAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken token)
    { await foreach (var item in ((IResponsesAdapter)_inner).StreamAsync(request, context, token)) yield return item; }
}
