using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;

namespace Asgard.Loki.Embedding.Jina.Tests;

/// <summary>独立维护单/多向量和媒体/融合原生报文，不调用生产映射生成预期。</summary>
internal static class JinaEmbeddingFixtures
{
    /// <summary>八种完整请求及响应样本。</summary>
    public static IEnumerable<object[]> Cases() => new[] { "text", "media", "group", "multi", "multi-base64", "implicit-multi", "empty-list", "nullable" }.Select(n => new object[] { n });
    /// <summary>读取独立原生报文。</summary>
    internal static JsonObject Read(string name = "text", string field = "response")
    {
        using var stream = typeof(JinaEmbeddingFixtures).Assembly.GetManifestResourceStream("Asgard.Loki.Embedding.Jina.Tests.Fixtures.native.json")
            ?? throw new InvalidDataException("Jina 原生样本缺失。");
        return JsonNode.Parse(stream)![name]![field]!.AsObject();
    }
    /// <summary>原生参数与公共模型别名独立。</summary>
    internal static EmbeddingRequest Request(string name = "text") => new()
    { Model = "public", Parameters = Read(name, "parameters").ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal) };
    /// <summary>完整本地替代目标。</summary>
    internal static JinaEmbeddingAdapter Adapter(StubHttpHandler handler) => new(new StubHttpClientFactory(handler), new()
    { Id = "jina-vector", Model = "embedding-native", ApiKey = "jina-test-key", BaseAddress = new("https://jina.invalid/tenant/v1/") });
    /// <summary>使用现有能力模块，无 LLM、Session 或其他能力引用。</summary>
    internal static Asgard.Loki.Abstractions.ILokiClient Build(IEmbeddingAdapter adapter) => new LokiClientBuilder().AddEmbedding(new()
    { Routes = [new() { Model = "public", Targets = [adapter] }] }).Build();
    /// <summary>完整无网络 JSON 响应。</summary>
    internal static HttpResponseMessage Json(JsonObject data) => new(HttpStatusCode.OK)
    { Content = new StringContent(data.ToJsonString(), Encoding.UTF8, "application/json") };
}
