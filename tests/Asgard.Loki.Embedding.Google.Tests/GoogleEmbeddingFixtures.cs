using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;

namespace Asgard.Loki.Embedding.Google.Tests;

/// <summary>独立原生单条、同步批和配置外壳样本，不通过生产适配器生成预期值。</summary>
internal static class GoogleEmbeddingFixtures
{
    /// <summary>返回独立的源或完整期望 JSON。</summary>
    internal static JsonObject Read(string key)
    {
        using var stream = typeof(GoogleEmbeddingFixtures).Assembly.GetManifestResourceStream(
            "Asgard.Loki.Embedding.Google.Tests.Fixtures.native.json") ?? throw new InvalidDataException("Google 嵌入样本缺失。");
        return JsonNode.Parse(stream)![key]!.AsObject();
    }
    /// <summary>旧/新/ProtoJSON 别名配置及原生多模态容器。</summary>
    public static IEnumerable<object[]> Cases()
    {
        foreach (var name in new[] { "single-legacy", "single-config", "single-snake", "single-media", "batch-legacy", "batch-config" }) yield return [name];
    }
    /// <summary>操作由固定目标显式配置，不由生产代码推断。</summary>
    internal static GoogleEmbeddingOperation Operation(string name) => name.StartsWith("batch", StringComparison.Ordinal)
        ? GoogleEmbeddingOperation.BatchEmbedContents : GoogleEmbeddingOperation.EmbedContent;
    /// <summary>反序列化原生源请求，公共路由名和供应商正文分离。</summary>
    internal static EmbeddingRequest Request(string name = "single-config") => JsonSerializer.Deserialize<EmbeddingRequest>(Read(name).ToJsonString())!;
    /// <summary>创建本地替代响应。</summary>
    internal static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    /// <summary>构造完整 Google 目标，凭据只用于替代 HTTP。</summary>
    internal static GoogleEmbeddingAdapter Adapter(StubHttpHandler handler, GoogleEmbeddingOperation operation = GoogleEmbeddingOperation.EmbedContent)
        => new(new StubHttpClientFactory(handler), new() { Id = "google-vector", Model = "embedding-test", ApiKey = "google-embedding-key",
            BaseAddress = new("https://google.invalid/tenant/v1beta/"), Operation = operation });
    /// <summary>公共能力入口不引用 LLM 或其他供应商。</summary>
    internal static Asgard.Loki.Abstractions.ILokiClient Build(GoogleEmbeddingAdapter adapter)
        => new LokiClientBuilder().AddEmbedding(new() { Routes = [new() { Model = "public", Targets = [adapter] }] }).Build();
}
