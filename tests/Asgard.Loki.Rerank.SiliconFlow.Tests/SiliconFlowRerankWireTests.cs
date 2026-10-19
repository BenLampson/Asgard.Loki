using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Rerank.SiliconFlow.Tests;

/// <summary>通过公共 SDK 验证独立完整原生报文、索引关联和未改写的上游结果。</summary>
public sealed class SiliconFlowRerankWireTests
{
    /// <summary>六份独立金样逐项验证认证、前缀、模型、原生容器、可选元数据和返回次序。</summary>
    [Theory] [MemberData(nameof(SiliconFlowRerankFixtures.Cases), MemberType = typeof(SiliconFlowRerankFixtures))]
    public async Task PublicSdk_MatchesFullWireAndNativeResult(string name)
    {
        var raw = SiliconFlowRerankFixtures.Read(name);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://silicon.invalid/prefix/v1/rerank", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer silicon-test-key", message.Headers.Authorization!.ToString()); Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            Assert.True(JsonNode.DeepEquals(SiliconFlowRerankFixtures.Read(name, "wire"), JsonNode.Parse(await message.Content.ReadAsStringAsync(token))));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(raw.ToJsonString())) };
            response.Content.Headers.ContentType = new("application/json"); return response;
        });
        await using var client = SiliconFlowRerankFixtures.Build(SiliconFlowRerankFixtures.Adapter(handler)); var request = SiliconFlowRerankFixtures.Request(name);
        var before = JsonSerializer.Serialize(request); var result = await client.Rerank.RerankAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(before, JsonSerializer.Serialize(request)); SiliconFlowRerankFixtures.Equal(raw, result.Data);
        Assert.Equal("public", result.Metadata.PublicModel); Assert.Equal("rerank-native", result.Metadata.UpstreamModel); Assert.Equal("silicon-ranking", result.Metadata.ProviderId);
        var rankings = result.SiliconFlowRankings; var expected = raw["results"]!.AsArray(); Assert.Equal(expected.Count, rankings.Count);
        for (var i = 0; i < rankings.Count; i++)
        {
            Assert.Equal(expected[i]!["index"]!.GetValue<int>(), rankings[i].Index); Assert.Equal(expected[i]!["relevance_score"]!.GetValue<double>(), rankings[i].RelevanceScore);
            SiliconFlowRerankFixtures.Equal(expected[i]!, rankings[i].Data);
        }
        Assert.False(result.Data.TryGetProperty("model", out _)); Assert.False(result.Data.TryGetProperty("usage", out _)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>没有已核对的单位区间约束；有限分数原样保留，便捷浮点视图不重写原始精度。</summary>
    [Theory] [InlineData("-2.5")] [InlineData("3.25")] [InlineData("1e-10000")]
    public async Task FiniteScores_AreNotNormalized(string score)
    {
        var raw = SiliconFlowRerankFixtures.Read(); raw["results"]![0]!["relevance_score"] = JsonNode.Parse(score);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(SiliconFlowRerankFixtures.Json(raw)));
        var result = await SiliconFlowRerankFixtures.Adapter(handler).RerankAsync(SiliconFlowRerankFixtures.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(score, result.SiliconFlowRankings[0].Data.GetProperty("relevance_score").GetRawText()); Assert.Equal(new[] { 2, 0, 1 }, result.SiliconFlowRankings.Select(item => item.Index));
    }
    /// <summary>大整数不会被收窄、改写或按最大值分配数组；top_n 仍受实际输入数量约束。</summary>
    [Theory] [InlineData("2147483648")] [InlineData("9007199254740993")] [InlineData("1e10000")]
    public async Task LargeTopNAndCounts_RemainExact(string number)
    {
        var parameters = SiliconFlowRerankFixtures.Read("array", "parameters"); parameters["top_n"] = JsonNode.Parse(number); parameters["max_chunks_per_doc"] = JsonNode.Parse(number);
        var raw = SiliconFlowRerankFixtures.Read(); raw["meta"]!["tokens"]!["input_tokens"] = JsonNode.Parse(number);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token)); Assert.Equal(number, body.RootElement.GetProperty("top_n").GetRawText());
            Assert.Equal(number, body.RootElement.GetProperty("max_chunks_per_doc").GetRawText()); return SiliconFlowRerankFixtures.Json(raw);
        });
        var result = await SiliconFlowRerankFixtures.Adapter(handler).RerankAsync(SiliconFlowRerankFixtures.Request(parameters), TestContext.Current.CancellationToken);
        Assert.Equal(number, result.Data.GetProperty("meta").GetProperty("tokens").GetProperty("input_tokens").GetRawText()); Assert.Equal(3, result.SiliconFlowRankings.Count);
    }
    /// <summary>原生精确整数指数/小数表达被保留；不是近似整数的数值由独立失败用例拒绝。</summary>
    [Theory] [InlineData("2e0", "0e10000")] [InlineData("2.0", "8e1")]
    public async Task ExactIntegerSpelling_IsPreserved(string top, string overlap)
    {
        var parameters = SiliconFlowRerankFixtures.Read("array", "parameters"); parameters["top_n"] = JsonNode.Parse(top); parameters["overlap_tokens"] = JsonNode.Parse(overlap);
        var raw = SiliconFlowRerankFixtures.Read(); raw["results"]!.AsArray().RemoveAt(2);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token)); Assert.Equal(top, body.RootElement.GetProperty("top_n").GetRawText());
            Assert.Equal(overlap, body.RootElement.GetProperty("overlap_tokens").GetRawText()); return SiliconFlowRerankFixtures.Json(raw);
        });
        await SiliconFlowRerankFixtures.Adapter(handler).RerankAsync(SiliconFlowRerankFixtures.Request(parameters), TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>等待期间改写调用方字典不改变已经发出的请求，也不会把返回关联校验移到新文档。</summary>
    [Fact]
    public async Task RequestSnapshot_IsIndependentDuringAwait()
    {
        var request = SiliconFlowRerankFixtures.Request("mixed");
        using var handler = new StubHttpHandler((_, _) =>
        {
            request.Parameters["documents"] = JsonSerializer.SerializeToElement(new[] { "replaced" });
            request.Parameters["top_n"] = JsonSerializer.SerializeToElement(1); return Task.FromResult(SiliconFlowRerankFixtures.Json(SiliconFlowRerankFixtures.Read("mixed")));
        });
        var result = await SiliconFlowRerankFixtures.Adapter(handler).RerankAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 2, 1 }, result.SiliconFlowRankings.Select(item => item.Index)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>重复值对应不同原始索引；可选 null 文档与省略保持可区分。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DuplicateValuesAndOptionalDocuments_PreserveIdentity(bool explicitNull)
    {
        var parameters = SiliconFlowRerankFixtures.Read("array", "parameters"); parameters["documents"] = new JsonArray("same", "same", "same");
        var raw = SiliconFlowRerankFixtures.Read(); if (explicitNull) raw["results"]![0]!["document"] = null;
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(SiliconFlowRerankFixtures.Json(raw)));
        var result = await SiliconFlowRerankFixtures.Adapter(handler).RerankAsync(SiliconFlowRerankFixtures.Request(parameters), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 2, 0, 1 }, result.SiliconFlowRankings.Select(item => item.Index));
        if (explicitNull) Assert.Equal(JsonValueKind.Null, result.SiliconFlowRankings[0].Document!.Value.ValueKind); else Assert.Null(result.SiliconFlowRankings[0].Document);
    }
    /// <summary>独立构造视图仅能验证原生结构，不能证明一个合法索引属于未知的原请求。</summary>
    [Fact]
    public void Views_OwnSnapshotsWithoutInventingRequestContext()
    {
        using var document = JsonDocument.Parse("""{"id":"raw","results":[{"index":20,"relevance_score":0,"document":{"text":"standalone"}}]}""");
        var result = new RerankResult(document.RootElement, new("public", "native", "custom")); var item = new SiliconFlowRerankItem(document.RootElement.GetProperty("results")[0]);
        document.Dispose(); Assert.Equal(20, result.SiliconFlowRankings[0].Index); Assert.Equal(20, item.Index); Assert.Equal("standalone", item.Document!.Value.GetProperty("text").GetString());
    }
}
