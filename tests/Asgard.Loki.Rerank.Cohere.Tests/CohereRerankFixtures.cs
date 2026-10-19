using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;

namespace Asgard.Loki.Rerank.Cohere.Tests;

/// <summary>独立编写的原生请求和完整报文样本，预期值不由生产适配器生成。</summary>
internal static class CohereRerankFixtures
{
    /// <summary>读取本次独立样本，不共享可变 JSON 节点。</summary>
    internal static JsonObject Read(string key, string part = "response")
    {
        using var stream = typeof(CohereRerankFixtures).Assembly.GetManifestResourceStream(
            "Asgard.Loki.Rerank.Cohere.Tests.Fixtures.native.json") ?? throw new InvalidDataException("重排样本缺失。");
        return JsonNode.Parse(stream)![key]![part]!.AsObject();
    }
    /// <summary>从标准 SDK 原生 JSON 请求入口建立测试请求。</summary>
    internal static RerankRequest Request(string key = "v2-basic") => JsonSerializer.Deserialize<RerankRequest>(Read(key, "request").ToJsonString())!;
    /// <summary>借用替代工厂，不注册其他能力或缓存。</summary>
    internal static CohereRerankAdapter Adapter(StubHttpHandler handler, CohereRerankApiVersion version = CohereRerankApiVersion.V2)
        => new(new StubHttpClientFactory(handler), new() { Model = "native-rerank", ApiVersion = version, ApiKey = "cohere-fixture-key",
            Id = "cohere-one", BaseAddress = new("https://rerank.invalid/tenant/"), ClientName = "test-project" });
    /// <summary>只使用独立重排模块的公开构建和调用链。</summary>
    internal static Asgard.Loki.Abstractions.ILokiClient Build(IRerankAdapter adapter)
        => new LokiClientBuilder().AddRerank(new() { Routes = [new() { Model = "public", Targets = [adapter] }] }).Build();
    /// <summary>构造内存原生 JSON 响应，不连接网络。</summary>
    internal static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
