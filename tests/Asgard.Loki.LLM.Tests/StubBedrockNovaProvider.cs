using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Tests;

/// <summary>调用方适配器替身，只实现独立 Nova 契约，不依赖 AWS 包。</summary>
internal sealed class StubBedrockNovaProvider : IBedrockNovaAdapter
{
    public string Id => "nova-contract";
    internal JsonObject? LastRequest { get; private set; }
    internal LlmAdapterContext? LastContext { get; private set; }
    internal Action? OnComplete { get; init; }
    internal int Calls { get; private set; }
    public Task<BedrockNovaResponse> InvokeAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls++; LastRequest = request; LastContext = context; OnComplete?.Invoke(); return Task.FromResult(new BedrockNovaResponse(JsonSerializer.SerializeToElement(new { output = new { message = new { role = "assistant", content = new[] { new { text = "native" } } } }, stopReason = "end_turn", future = (object?)null }), context.Metadata));
    }
    public async IAsyncEnumerable<BedrockNovaEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await InvokeAsync(request, context, cancellationToken); yield return new(JsonSerializer.SerializeToElement(new { contentBlockDelta = new { delta = new { text = "native" }, contentBlockIndex = 7 } }), context.Metadata, "chunk", "contentBlockDelta"); yield return new(JsonSerializer.SerializeToElement(new { messageStop = new { stopReason = "end_turn" } }), context.Metadata, "chunk", "messageStop");
    }
}
