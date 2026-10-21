using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Tests;

/// <summary>调用方适配器替身，只实现独立 Converse 契约，不依赖 AWS 包。</summary>
internal sealed class StubBedrockConverseProvider : IBedrockConverseAdapter
{
    public string Id => "bedrock-contract";
    internal JsonObject? LastRequest { get; private set; }
    internal LlmAdapterContext? LastContext { get; private set; }
    internal Action? OnComplete { get; init; }
    internal int Calls { get; private set; }
    public Task<BedrockConverseResponse> ConverseAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls++; LastRequest = request; LastContext = context; OnComplete?.Invoke(); return Task.FromResult(new BedrockConverseResponse(JsonSerializer.SerializeToElement(new { output = new { message = new { role = "assistant", content = new[] { new { text = "native" } } } }, stopReason = "end_turn", future = (object?)null }), context.Metadata));
    }
    public async IAsyncEnumerable<BedrockConverseEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await ConverseAsync(request, context, cancellationToken); yield return new(JsonSerializer.SerializeToElement(new { delta = new { text = "native" }, contentBlockIndex = 7 }), context.Metadata, "contentBlockDelta"); yield return new(JsonSerializer.SerializeToElement(new { stopReason = "end_turn" }), context.Metadata, "messageStop");
    }
}
