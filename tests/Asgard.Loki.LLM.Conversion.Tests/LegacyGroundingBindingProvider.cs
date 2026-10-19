using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>仅用于模拟上一个转换算法的真实 Session 指纹，不修改缓存或伪造 HTTP 成功。</summary>
internal sealed class LegacyGroundingBindingProvider(ConvertingLlmProvider inner) : IGeminiAdapter
{
    /// <inheritdoc />
    public string Id => inner.Id;
    /// <inheritdoc />
    public string BindingIdentity => inner.BindingIdentity.Replace(":conversion-v22:", ":conversion-v21:", StringComparison.Ordinal);
    /// <inheritdoc />
    public Task<GeminiContent> GenerateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => inner.GenerateAsync(request, context, cancellationToken);
    /// <inheritdoc />
    public IAsyncEnumerable<GeminiContentEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => ((IGeminiAdapter)inner).StreamAsync(request, context, cancellationToken);
}
