using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Rerank.OpenAICompatible.Tests;

/// <summary>源协议可直接委托的显式目标根路径，不对供应商品牌或模型名做自动路由。</summary>
public sealed class CompatibleRerankProfileTests
{
    /// <summary>完整原生请求/响应在通用、Moonshot 风格和自定义前缀目标中保持，凭据/身份/模型独立。</summary>
    [Theory]
    [InlineData("https://generic-rerank.example.invalid/v1/", "https://generic-rerank.example.invalid/v1/rerank", "generic-profile", "fixture-generic-token", "generic-client")]
    [InlineData("https://moonshot-style.example.invalid/v1/", "https://moonshot-style.example.invalid/v1/rerank", "moonshot-style-profile", "fixture-moonshot-token", "moonshot-client")]
    [InlineData("https://custom-rerank.example.invalid/tenant/v1/", "https://custom-rerank.example.invalid/tenant/v1/rerank", "explicit-custom-profile", "fixture-custom-token", "custom-client")]
    public async Task ExplicitCompatibleTargetRetainsNativeProtocol(string root, string endpoint, string id, string token, string clientName)
    {
        var options = new OpenAiCompatibleRerankOptions
        { Id = id, BaseAddress = new(root), Model = "caller-rerank-model", Authentication = OpenAiCompatibleRerankAuthentication.BearerToken, ApiKey = token, HttpClientName = clientName };
        using var factory = CompatibleRerankFixture.Factory(2); factory.OnSend = async (message, cancellation) =>
        {
            Assert.Equal(endpoint, message.RequestUri!.AbsoluteUri); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(token, message.Headers.Authorization.Parameter);
            Assert.True(JsonElement.DeepEquals(CompatibleRerankFixture.Part("requests")[2], CompatibleRerankFixture.Element(await message.Content!.ReadAsStringAsync(cancellation))));
            return CompatibleRerankFixture.Json(CompatibleRerankFixture.Part("responses")[2]);
        };
        await using var client = new LokiClientBuilder().AddRerank(new() { Routes = [new() { Model = "public-ranker", Targets = [new OpenAiCompatibleRerankAdapter(factory, options)] }] }).Build();
        var result = await client.Rerank.RerankAsync(CompatibleRerankFixture.Request(2), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(CompatibleRerankFixture.Part("responses")[2], result.Data)); Assert.Equal(new RerankResultMetadata("public-ranker", options.Model, id), result.Metadata);
        Assert.Equal(clientName, factory.ClientName); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }
}
