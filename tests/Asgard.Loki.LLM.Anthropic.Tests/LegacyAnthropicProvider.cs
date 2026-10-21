using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>按前一版供应商身份写入真实缓存，用于迁移回归；不改写核心指纹算法。</summary>
internal sealed class LegacyAnthropicProvider(AnthropicLlmProvider current) : IClaudeMessagesAdapter
{
    /// <inheritdoc />
    public string Id => current.Id;
    /// <inheritdoc />
    public string BindingIdentity => typeof(AnthropicLlmProvider).AssemblyQualifiedName + ":2023-06-01:Asgard.Loki.LLM.Anthropic";
    /// <inheritdoc />
    public Task<ClaudeMessage> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => current.CreateAsync(request, context, cancellationToken);
    /// <inheritdoc />
    public IAsyncEnumerable<ClaudeMessageEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => current.StreamAsync(request, context, cancellationToken);
}
