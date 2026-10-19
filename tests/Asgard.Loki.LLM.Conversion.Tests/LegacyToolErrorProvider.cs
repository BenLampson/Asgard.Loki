using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>借用当前传输，独立提供真实旧版选项外壳/算法身份以建立缓存绑定。</summary>
internal sealed class LegacyToolErrorProvider(ConvertingLlmProvider inner, string identity) : IGeminiAdapter
{
    /// <inheritdoc />
    public string Id => inner.Id;
    /// <inheritdoc />
    public string BindingIdentity => identity;
    /// <inheritdoc />
    public Task<GeminiContent> GenerateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default) => inner.GenerateAsync(request, context, cancellationToken);
    /// <inheritdoc />
    public IAsyncEnumerable<GeminiContentEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default) => ((IGeminiAdapter)inner).StreamAsync(request, context, cancellationToken);
}
