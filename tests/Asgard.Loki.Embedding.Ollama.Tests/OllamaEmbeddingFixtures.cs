using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;

namespace Asgard.Loki.Embedding.Ollama.Tests;

/// <summary>独立维护原生请求与响应样本，不使用生产适配器生成预期。</summary>
internal static class OllamaEmbeddingFixtures
{
    /// <summary>标量、批、图片、音频、混合输入与显式空调用。</summary>
    public static IEnumerable<object[]> Cases() => new[] { "single", "batch", "image", "audio", "mixed", "empty" }.Select(n => new object[] { n });

    /// <summary>读取独立嵌入报文。</summary>
    internal static JsonObject Read(string name = "single", string field = "response")
    {
        using var stream = typeof(OllamaEmbeddingFixtures).Assembly.GetManifestResourceStream("Asgard.Loki.Embedding.Ollama.Tests.Fixtures.native.json")
            ?? throw new InvalidDataException("原生 Ollama 样本缺失。");
        return JsonNode.Parse(stream)![name]![field]!.AsObject();
    }
    /// <summary>原生参数保持 JsonElement，不引入公共统一输入模型。</summary>
    internal static EmbeddingRequest Request(string name = "single") => Request(Read(name, "parameters"));
    /// <summary>从测试原生对象创建请求。</summary>
    internal static EmbeddingRequest Request(JsonObject parameters) => new()
    {
        Model = "public", Parameters = parameters.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal)
    };
    /// <summary>无网络的固定目标；可选显式 Key 不借用其他能力。</summary>
    internal static OllamaEmbeddingAdapter Adapter(StubHttpHandler handler, string? key = null) => new(new StubHttpClientFactory(handler), new()
    { Id = "ollama-vector", Model = "team/embed-test:tag", BaseAddress = new("https://ollama.invalid/tenant/api/"), ApiKey = key });
    /// <summary>使用现有能力注册与公共 SDK 入口。</summary>
    internal static Asgard.Loki.Abstractions.ILokiClient Build(IEmbeddingAdapter adapter) => new LokiClientBuilder().AddEmbedding(new()
    { Routes = [new() { Model = "public", Targets = [adapter] }] }).Build();
    /// <summary>独立完整 JSON 响应。</summary>
    internal static HttpResponseMessage Json(JsonObject data) => new(HttpStatusCode.OK)
    { Content = new StringContent(data.ToJsonString(), Encoding.UTF8, "application/json") };
}
