using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;

namespace Asgard.Loki.Rerank.Ali.Tests;

/// <summary>独立原生嵌套报文，不通过生产代码生成预期结果。</summary>
internal static class AliRerankFixtures
{
    /// <summary>六种完整原生调用样本。</summary>
    public static IEnumerable<object[]> Cases() => new[] { "text", "top", "oversize", "mixed", "image", "echo" }.Select(n => new object[] { n });
    /// <summary>读取完整原生请求、最终正文或响应。</summary>
    internal static JsonObject Read(string name = "text", string field = "response")
    {
        using var stream = typeof(AliRerankFixtures).Assembly.GetManifestResourceStream("Asgard.Loki.Rerank.Ali.Tests.Fixtures.native.json")
            ?? throw new InvalidDataException("DashScope 原生样本缺失。");
        return JsonNode.Parse(stream)![name]![field]!.AsObject();
    }
    /// <summary>公共模型别名与原生参数独立。</summary>
    internal static RerankRequest Request(string name = "text") => Request(Read(name, "parameters"));
    /// <summary>原始原生参数快照。</summary>
    internal static RerankRequest Request(JsonObject parameters) => new()
    { Model = "public", Parameters = parameters.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal) };
    /// <summary>纯替代 HTTP 固定目标，不使用真实 Key 或其他供应商。</summary>
    internal static AliRerankAdapter Adapter(StubHttpHandler handler) => new(new StubHttpClientFactory(handler), new()
    { Id = "ali-ranking", Model = "rerank-native", ApiKey = "ali-test-key", BaseAddress = new("https://ali.invalid/workspace/api/v1/") });
    /// <summary>复用现有 Rerank 模块注册，供应商不依赖入口包。</summary>
    internal static Asgard.Loki.Abstractions.ILokiClient Build(IRerankAdapter adapter) => new LokiClientBuilder().AddRerank(new()
    { Routes = [new() { Model = "public", Targets = [adapter] }] }).Build();
    /// <summary>完整本地 JSON 响应。</summary>
    internal static HttpResponseMessage Json(JsonObject data) => new(HttpStatusCode.OK)
    { Content = new StringContent(data.ToJsonString(), Encoding.UTF8, "application/json") };
}
