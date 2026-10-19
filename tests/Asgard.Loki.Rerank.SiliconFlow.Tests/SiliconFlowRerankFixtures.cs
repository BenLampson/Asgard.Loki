using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Rerank.SiliconFlow.Tests;

/// <summary>完整原生请求/响应均独立编写，不由供应商实现生成预期，也不请求真实模型。</summary>
public static class SiliconFlowRerankFixtures
{
    /// <summary>六个独立线路样本覆盖标量、批量、混合媒体、设置、图片及空文本语义。</summary>
    public static TheoryData<string> Cases => ["scalar", "array", "mixed", "settings", "image", "empty-text"];
    /// <summary>每次读取独立嵌入样本，不跨测试共享可变节点。</summary>
    internal static JsonObject Read(string name = "array", string section = "response")
    {
        using var stream = typeof(SiliconFlowRerankFixtures).Assembly.GetManifestResourceStream(
            "Asgard.Loki.Rerank.SiliconFlow.Tests.Fixtures.native.json") ?? throw new InvalidDataException("SiliconFlow 样本缺失。");
        return JsonNode.Parse(stream)![name]![section]!.AsObject();
    }
    /// <summary>公开核心请求保留原生参数，公共模型只用作 SDK 路由。</summary>
    internal static RerankRequest Request(string name = "array") => Request(Read(name, "parameters"));
    /// <summary>参数作为独立原生 JSON 元素传入，不混用其他供应商模型。</summary>
    internal static RerankRequest Request(JsonObject parameters) => new()
    { Model = "public", Parameters = parameters.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal) };
    /// <summary>测试目标保留版本与代理前缀；假 Key 只用于本地认证头断言。</summary>
    internal static SiliconFlowRerankAdapter Adapter(HttpMessageHandler handler)
        => new(new StubHttpClientFactory(handler), Options());
    /// <summary>可重复创建完整固定目标。</summary>
    internal static SiliconFlowRerankOptions Options() => new()
    { Id = "silicon-ranking", Model = "rerank-native", ApiKey = "silicon-test-key", BaseAddress = new("https://silicon.invalid/prefix/v1/") };
    /// <summary>使用标准公共 SDK 重排入口，不引入 Session 或其他能力。</summary>
    internal static ILokiClient Build(IRerankAdapter adapter) => new LokiClientBuilder().AddRerank(new()
    { Routes = [new() { Model = "public", Targets = [adapter] }] }).Build();
    /// <summary>原生 JSON 回包，不替供应商生成关联或补默认元数据。</summary>
    internal static HttpResponseMessage Json(JsonNode data) => new(HttpStatusCode.OK)
    { Content = new StringContent(data.ToJsonString(), Encoding.UTF8, "application/json") };
    /// <summary>完整 JSON 比较不依赖属性插入次序，但保留容器和数组顺序。</summary>
    internal static void Equal(JsonNode expected, JsonElement actual) => Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(actual.GetRawText())), actual.GetRawText());
}
