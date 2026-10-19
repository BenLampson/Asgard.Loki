using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Rerank.Ali.Tests;

/// <summary>公共 SDK 验证原生外壳、媒体容器、返回选项和排名顺序，不进行 SDK 侧检索。</summary>
public sealed class AliRerankWireTests
{
    /// <summary>六份完整 HTTP 金样使用分片正文；未知字段、Unicode、数字和未排序的原始索引均保留。</summary>
    [Theory] [MemberData(nameof(AliRerankFixtures.Cases), MemberType = typeof(AliRerankFixtures))]
    public async Task NativePipeline_MatchesIndependentGolden(string name)
    {
        var raw = AliRerankFixtures.Read(name);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://ali.invalid/workspace/api/v1/services/rerank/text-rerank/text-rerank", message.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            Assert.Equal("ali-test-key", message.Headers.Authorization.Parameter); Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            Assert.True(JsonNode.DeepEquals(AliRerankFixtures.Read(name, "wire"), JsonNode.Parse(await message.Content.ReadAsStringAsync(token))));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(raw.ToJsonString())) };
            response.Content.Headers.ContentType = new("application/json"); return response;
        });
        await using var client = AliRerankFixtures.Build(AliRerankFixtures.Adapter(handler)); var request = AliRerankFixtures.Request(name);
        var before = JsonSerializer.Serialize(request); var result = await client.Rerank.RerankAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.True(JsonNode.DeepEquals(raw, JsonNode.Parse(result.Data.GetRawText())));
        Assert.Equal("public", result.Metadata.PublicModel); Assert.Equal("rerank-native", result.Metadata.UpstreamModel); Assert.Equal("ali-ranking", result.Metadata.ProviderId);
        var rankings = result.AliRankings; var expected = raw["output"]!["results"]!.AsArray(); Assert.Equal(expected.Count, rankings.Count);
        for (var i = 0; i < rankings.Count; i++)
        {
            Assert.Equal(expected[i]!["index"]!.GetValue<int>(), rankings[i].Index); Assert.Equal(expected[i]!["relevance_score"]!.GetValue<double>(), rankings[i].RelevanceScore);
            Assert.True(JsonNode.DeepEquals(expected[i], JsonNode.Parse(rankings[i].Data.GetRawText())));
        }
        Assert.False(result.Data.TryGetProperty("model", out _)); Assert.False(result.Data.TryGetProperty("results", out _)); Assert.Equal(1, handler.Calls);
    }

    /// <summary>原生精确整数表示和零至一边界数值不被浮点近似重写。</summary>
    [Theory] [InlineData("2e0", "0e10000")] [InlineData("2.0", "1.0000")] [InlineData("2", "1e-10000")]
    public async Task ExactNumericRepresentations_ArePreserved(string top, string score)
    {
        var parameters = AliRerankFixtures.Read("top", "parameters"); parameters["parameters"]!["top_n"] = JsonNode.Parse(top);
        var raw = AliRerankFixtures.Read("top"); raw["output"]!["results"]![0]!["relevance_score"] = JsonNode.Parse(score);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal(top, body.RootElement.GetProperty("parameters").GetProperty("top_n").GetRawText()); return AliRerankFixtures.Json(raw);
        });
        var result = await AliRerankFixtures.Adapter(handler).RerankAsync(AliRerankFixtures.Request(parameters), TestContext.Current.CancellationToken);
        Assert.Equal(score, result.AliRankings[0].Data.GetProperty("relevance_score").GetRawText());
    }

    /// <summary>相同文本可以属于不同原始索引，不能去重；原生缺失或 null 文档保持可区分。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DuplicateTextAndOptionalDocument_KeepNativeIdentity(bool explicitNull)
    {
        var parameters = AliRerankFixtures.Read("text", "parameters"); parameters["input"]!["documents"] = new JsonArray("same", "same", "third");
        var raw = AliRerankFixtures.Read(); if (explicitNull) raw["output"]!["results"]![0]!["document"] = null;
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(AliRerankFixtures.Json(raw)));
        var result = await AliRerankFixtures.Adapter(handler).RerankAsync(AliRerankFixtures.Request(parameters), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 2, 0, 1 }, result.AliRankings.Select(r => r.Index));
        if (explicitNull) Assert.Equal(JsonValueKind.Null, result.AliRankings[0].Document!.Value.ValueKind); else Assert.Null(result.AliRankings[0].Document);
        Assert.Equal(9007199254740993L, result.Data.GetProperty("usage").GetProperty("total_tokens").GetInt64());
        Assert.False(result.Data.GetProperty("usage").TryGetProperty("prompt_tokens", out _));
    }

    /// <summary>超过本地数组范围的精确 top_n 不做截断改写，返回数量仍不得超过原始文档。</summary>
    [Theory] [InlineData("2147483648")] [InlineData("9007199254740993")] [InlineData("1e10000")]
    public async Task LargeTopN_IsNotNarrowedOrDefaulted(string number)
    {
        var parameters = AliRerankFixtures.Read("text", "parameters"); parameters["parameters"] = new JsonObject { ["top_n"] = JsonNode.Parse(number) };
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal(number, body.RootElement.GetProperty("parameters").GetProperty("top_n").GetRawText()); return AliRerankFixtures.Json(AliRerankFixtures.Read());
        });
        var result = await AliRerankFixtures.Adapter(handler).RerankAsync(AliRerankFixtures.Request(parameters), TestContext.Current.CancellationToken);
        Assert.Equal(3, result.AliRankings.Count);
    }
}
