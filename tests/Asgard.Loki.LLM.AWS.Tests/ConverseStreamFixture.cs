using System.Reflection;
using System.Text.Json;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>Converse 原始帧 JSON 没有 Claude Base64 包装，未知载荷保持原形。</summary>
internal static class ConverseStreamFixture
{
    internal static byte[] Bytes()
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Asgard.Loki.LLM.AWS.Tests.Fixtures.converse.eventstream")!; using var output = new MemoryStream(); source.CopyTo(output); return output.ToArray();
    }
    internal static byte[] Event(string name, string json) => StreamFixture.Frame("event", name, json);
    internal static byte[] Start() => Event("messageStart", "{\"role\":\"assistant\"}");
    internal static byte[] Stop(string reason = "end_turn") => Event("messageStop", JsonSerializer.Serialize(new { stopReason = reason }));
    internal static byte[] Metadata() => Event("metadata", "{\"usage\":{\"inputTokens\":2,\"outputTokens\":1,\"totalTokens\":3},\"metrics\":{\"latencyMs\":1}}");
    internal static byte[] Complete() => [.. Start(), .. Stop(), .. Metadata()];
    internal static async Task Consume(AwsLlmProvider provider, CancellationToken token, Action<BedrockConverseEvent>? observe = null)
    {
        await foreach (var item in provider.ConverseStreamAsync(ConverseFixture.Request(), ConverseFixture.Context(), token)) observe?.Invoke(item);
    }
}
