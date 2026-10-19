using System.Reflection;
using System.Text.Json;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>独立二进制金样与测试侧 CRC 编码；不通过生产解码器构造预期。</summary>
internal static class NovaStreamFixture
{
    /// <summary>逐字节网络与原始帧断言使用同一独立嵌入二进制样本。</summary>
    internal static byte[] Bytes()
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Asgard.Loki.LLM.AWS.Tests.Fixtures.nova.eventstream")!; using var result = new MemoryStream(); source.CopyTo(result); return result.ToArray();
    }
    /// <summary>完整内层信封原样放入外层 bytes，不调用生产解析器。</summary>
    internal static byte[] Chunk(string json) => StreamFixture.Chunk(json);
    internal static byte[] Start() => Chunk("{\"messageStart\":{\"role\":\"assistant\"}}");
    internal static byte[] Stop() => Chunk("{\"messageStop\":{\"stopReason\":\"end_turn\"}}");
    internal static byte[] Metadata() => Chunk("{\"metadata\":{\"usage\":{\"inputTokens\":2,\"outputTokens\":1,\"totalTokens\":3}}}");
    internal static byte[] Complete() => [.. Start(), .. Stop(), .. Metadata()];
    /// <summary>同时测试本次提前交付内容和暂存成功是否逃逸。</summary>
    internal static async Task Drain(AwsLlmProvider provider, CancellationToken token, List<BedrockNovaEvent> seen, Action<BedrockNovaEvent>? observe = null, bool apiKey = false)
    {
        await foreach (var item in provider.NovaStreamAsync(NovaFixture.Request(), NovaFixture.Context(apiKey: apiKey), token)) { seen.Add(item); observe?.Invoke(item); }
    }
    /// <summary>从独立事件清单取原始内层对象。</summary>
    internal static JsonElement Events() => JsonSerializer.Deserialize<JsonElement>(AwsFixture.Text("nova-stream-events"));
}
