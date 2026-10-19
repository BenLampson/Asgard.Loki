using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;

namespace Asgard.Loki.Embedding.OpenAI.Tests;

/// <summary>独立原生输入、完整最终请求与响应样本，不调用生产实现生成预期报文。</summary>
internal static class OpenAiEmbeddingFixtures
{
    /// <summary>每次读取独立可变样本。</summary>
    internal static JsonObject Read(string key)
    {
        using var stream = typeof(OpenAiEmbeddingFixtures).Assembly.GetManifestResourceStream(
            "Asgard.Loki.Embedding.OpenAI.Tests.Fixtures.create.json") ?? throw new InvalidDataException("嵌入样本缺失。");
        return JsonNode.Parse(stream)![key]!.AsObject();
    }
    /// <summary>从原生 JSON 入口反序列化请求，包含全部扩展字段。</summary>
    internal static EmbeddingRequest Request(string input = "strings", string encoding = "float")
        => JsonSerializer.Deserialize<EmbeddingRequest>(Read("request-" + input + "-" + encoding).ToJsonString())!;
    /// <summary>四种原生输入与两种输出编码。</summary>
    public static IEnumerable<object[]> Cases()
    {
        foreach (var input in new[] { "text", "strings", "tokens", "token-batch" })
            foreach (var encoding in new[] { "float", "base64" }) yield return [input, encoding];
    }
    /// <summary>本地替代 JSON 响应。</summary>
    internal static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    /// <summary>构造独立固定嵌入目标。</summary>
    internal static OpenAiEmbeddingAdapter Adapter(StubHttpHandler handler, string root = "https://embed.invalid/tenant/v1/")
        => new(new StubHttpClientFactory(handler), new() { Model = "text-embedding-test", ApiKey = "embedding-key",
            Id = "embedding-one", BaseAddress = new(root), Organization = "org", Project = "project" });
    /// <summary>只注册嵌入能力，不需要 LLM、缓存或其他供应商配置。</summary>
    internal static Asgard.Loki.Abstractions.ILokiClient Build(OpenAiEmbeddingAdapter adapter)
        => new LokiClientBuilder().AddEmbedding(new() { Routes = [new() { Model = "public", Targets = [adapter] }] }).Build();
}
