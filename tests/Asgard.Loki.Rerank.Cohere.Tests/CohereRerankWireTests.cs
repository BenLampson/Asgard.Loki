using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Rerank.Cohere.Tests;

/// <summary>通过公共 SDK 比较独立完整报文，验证原生参数、文档身份和未知字段保留。</summary>
public sealed class CohereRerankWireTests
{
    /// <summary>v1 文本/对象和 v2 默认/显式设置保持各自原生结构，不注入返回文档或 top_n 默认值。</summary>
    [Theory]
    [InlineData("v1-text", CohereRerankApiVersion.V1)] [InlineData("v1-object", CohereRerankApiVersion.V1)]
    [InlineData("v2-basic", CohereRerankApiVersion.V2)] [InlineData("v2-options", CohereRerankApiVersion.V2)]
    public async Task NativeCall_MatchesIndependentFullGolden(string key, CohereRerankApiVersion version)
    {
        var expected = CohereRerankFixtures.Read(key); var source = CohereRerankFixtures.Request(key); var before = JsonSerializer.Serialize(source);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal($"https://rerank.invalid/tenant/v{(int)version}/rerank", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("cohere-fixture-key", message.Headers.Authorization.Parameter);
            Assert.Equal("test-project", Assert.Single(message.Headers.GetValues("X-Client-Name")));
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("application/json", Assert.Single(message.Headers.Accept).MediaType);
            Assert.True(JsonNode.DeepEquals(CohereRerankFixtures.Read(key, "wire"), JsonNode.Parse(await message.Content.ReadAsStringAsync(token))));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(expected.ToJsonString())) };
        });
        await using var client = CohereRerankFixtures.Build(CohereRerankFixtures.Adapter(handler, version));
        var result = await client.Rerank.RerankAsync(source, TestContext.Current.CancellationToken);
        Assert.Equal(before, JsonSerializer.Serialize(source)); Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(result.Data.GetRawText())));
        Assert.Equal("public", result.Metadata.PublicModel); Assert.Equal("native-rerank", result.Metadata.UpstreamModel); Assert.Equal("cohere-one", result.Metadata.ProviderId);
        var items = result.CohereRankings; Assert.Equal(expected["results"]!.AsArray().Count, items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            Assert.True(JsonNode.DeepEquals(expected["results"]![index], JsonNode.Parse(items[index].Data.GetRawText())));
            Assert.Equal(expected["results"]![index]!["index"]!.GetValue<int>(), items[index].Index);
            Assert.Equal(expected["results"]![index]!["relevance_score"]!.GetValue<double>(), items[index].RelevanceScore);
        }
        Assert.NotEqual(0, items[0].Index); Assert.Equal(1, handler.Calls);
    }

    /// <summary>省略、false、null 扩展字段及本地计算上限保持区别；top_n 超过文档数也不改写请求。</summary>
    [Theory] [InlineData("absent")] [InlineData("false")] [InlineData("large-top")]
    public async Task V1Options_KeepCallerIntent(string kind)
    {
        var source = CohereRerankFixtures.Request();
        if (kind == "false") source.Parameters["return_documents"] = JsonSerializer.SerializeToElement(false);
        if (kind == "large-top") source.Parameters["top_n"] = JsonSerializer.SerializeToElement(10);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var wire = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!.AsObject();
            Assert.Equal(kind == "false", wire.ContainsKey("return_documents"));
            if (kind == "false") Assert.False(wire["return_documents"]!.GetValue<bool>());
            Assert.Equal(kind == "large-top", wire.ContainsKey("top_n"));
            if (kind == "large-top") Assert.Equal(10, wire["top_n"]!.GetValue<int>());
            Assert.False(wire.ContainsKey("rank_fields")); Assert.False(wire.ContainsKey("max_chunks_per_doc")); Assert.False(wire.ContainsKey("stream"));
            return CohereRerankFixtures.Json(CohereRerankFixtures.Read("v2-basic"));
        });
        await using var client = CohereRerankFixtures.Build(CohereRerankFixtures.Adapter(handler, CohereRerankApiVersion.V1));
        var result = await client.Rerank.RerankAsync(source, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.CohereRankings.Count); Assert.Null(result.CohereRankings[0].Document);
        Assert.False(result.Data.TryGetProperty("usage", out _)); Assert.False(result.Data.GetProperty("meta").TryGetProperty("tokens", out _));
    }

    /// <summary>原生整数的小数/指数表示保持原始报文，不因为 double 舍入而接受非整数。</summary>
    [Theory] [InlineData("1.0")] [InlineData("1e0")]
    public async Task ExactIntegerAndScoreRepresentations_AreRetained(string number)
    {
        var source = CohereRerankFixtures.Request(); source.Parameters["top_n"] = JsonSerializer.Deserialize<JsonElement>(number);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var wire = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal(number, wire.RootElement.GetProperty("top_n").GetRawText());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"results\":[{\"index\":1e0,\"relevance_score\":0.99999999999999999999}]}") };
        });
        var result = await CohereRerankFixtures.Adapter(handler).RerankAsync(source, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.CohereRankings[0].Index); Assert.Equal("0.99999999999999999999", result.CohereRankings[0].Data.GetProperty("relevance_score").GetRawText());
    }

    /// <summary>大小写不同的扩展 model 和其他方言字段作为原生扩展保留，不充当 SDK 版本选择器。</summary>
    [Fact]
    public async Task Extensions_AreNotFilteredOrUsedToSelectVersion()
    {
        var source = CohereRerankFixtures.Request();
        source = source with { Parameters = new(source.Parameters, StringComparer.OrdinalIgnoreCase) };
        source.Parameters["MODEL"] = JsonSerializer.SerializeToElement("extension"); source.Parameters["return_documents"] = JsonSerializer.SerializeToElement(false);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.EndsWith("/v2/rerank", message.RequestUri!.AbsolutePath);
            using var wire = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal("native-rerank", wire.RootElement.GetProperty("model").GetString());
            Assert.Equal("extension", wire.RootElement.GetProperty("MODEL").GetString()); Assert.False(wire.RootElement.GetProperty("return_documents").GetBoolean());
            return CohereRerankFixtures.Json(CohereRerankFixtures.Read("v2-basic"));
        });
        await CohereRerankFixtures.Adapter(handler).RerankAsync(source, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);
    }
}
