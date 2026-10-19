using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;

namespace Asgard.Loki.Rerank.Jina.Tests;

/// <summary>独立编写文本、多模态和 listwise 原生报文，不调用生产代码生成预期结果。</summary>
internal static class JinaRerankFixtures
{
    /// <summary>读取独立请求、最终报文或响应样本。</summary>
    internal static JsonObject Read(string key, string part = "response")
    {
        using var stream = typeof(JinaRerankFixtures).Assembly.GetManifestResourceStream("Asgard.Loki.Rerank.Jina.Tests.Fixtures.native.json")
            ?? throw new InvalidDataException("Jina 重排样本缺失。");
        return JsonNode.Parse(stream)![key]![part]!.AsObject();
    }
    /// <summary>从 SDK 的原生 JSON 请求入口构造参数。</summary>
    internal static RerankRequest Request(string key = "text") => JsonSerializer.Deserialize<RerankRequest>(Read(key, "request").ToJsonString())!;
    /// <summary>用固定假地址和替代 HTTP 工厂创建供应商。</summary>
    internal static JinaRerankAdapter Adapter(StubHttpHandler handler)
        => new(new StubHttpClientFactory(handler), new() { Model = "native-rerank", ApiKey = "jina-fixture-key", Id = "jina-one", BaseAddress = new("https://rerank.invalid/tenant/v1/") });
    /// <summary>只注册 Rerank，不引用 LLM、Embedding 或其他供应商。</summary>
    internal static Asgard.Loki.Abstractions.ILokiClient Build(IRerankAdapter adapter)
        => new LokiClientBuilder().AddRerank(new() { Routes = [new() { Model = "public", Targets = [adapter] }] }).Build();
    /// <summary>内存原生 JSON 响应。</summary>
    internal static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
