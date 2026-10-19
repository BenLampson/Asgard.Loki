using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;

namespace Asgard.Loki.Embedding.OpenAI.Tests;

/// <summary>Jina 公开原生 Schema 的兼容子集样本，不用 OpenAI 参数别名改写其设置。</summary>
internal static class JinaCompatibilityFixtures
{
    /// <summary>独立完整请求、最终正文和响应。</summary>
    internal static JsonObject Read(string name = "text", string field = "response")
    {
        using var stream = typeof(JinaCompatibilityFixtures).Assembly.GetManifestResourceStream(
            "Asgard.Loki.Embedding.OpenAI.Tests.Fixtures.jina-compatible.json") ?? throw new InvalidDataException("Jina 兼容样本缺失。");
        return JsonNode.Parse(stream)![name]![field]!.AsObject();
    }
    /// <summary>参数仍使用原生 JSON 扩展入口。</summary>
    internal static EmbeddingRequest Request(string name = "text") => JsonSerializer.Deserialize<EmbeddingRequest>(Read(name, "request").ToJsonString())!;
    /// <summary>固定完整兼容目标，不读取 Key、查询模型或选择另一家供应商。</summary>
    internal static OpenAiEmbeddingAdapter Adapter(StubHttpHandler handler, string model = "jina-embeddings-v3") => new(new StubHttpClientFactory(handler), new()
    { Id = "jina-compatible", Model = model, ApiKey = "jina-fixture-key", BaseAddress = new("https://jina.invalid/tenant/v1/") });
}
