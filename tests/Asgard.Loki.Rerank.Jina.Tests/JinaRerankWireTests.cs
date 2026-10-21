using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Rerank.Jina.Tests;

/// <summary>经公共 SDK 验证完整原生报文、模态、空值、文档关联和 listwise 向量。</summary>
public sealed class JinaRerankWireTests
{
    /// <summary>七份独立最终报文和完整响应保持原生结构，媒体内容不经过 SDK 下载或解码。</summary>
    [Theory] [InlineData("text")] [InlineData("objects")] [InlineData("nullable")] [InlineData("suppressed")]
    [InlineData("multimodal-text-query")] [InlineData("multimodal-image-query")] [InlineData("listwise")]
    public async Task NativeCall_MatchesIndependentFullGolden(string key)
    {
        var expected = JinaRerankFixtures.Read(key); var request = JinaRerankFixtures.Request(key); var before = JsonSerializer.Serialize(request);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://rerank.invalid/tenant/v1/rerank", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("jina-fixture-key", message.Headers.Authorization.Parameter);
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType); Assert.Equal("application/json", Assert.Single(message.Headers.Accept).MediaType);
            Assert.True(JsonNode.DeepEquals(JinaRerankFixtures.Read(key, "wire"), JsonNode.Parse(await message.Content.ReadAsStringAsync(token))));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(expected.ToJsonString())) };
        });
        await using var client = JinaRerankFixtures.Build(JinaRerankFixtures.Adapter(handler));
        var result = await client.Rerank.RerankAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(result.Data.GetRawText())));
        Assert.Equal("public", result.Metadata.PublicModel); Assert.Equal("native-rerank", result.Metadata.UpstreamModel); Assert.Equal("jina-one", result.Metadata.ProviderId);
        var items = result.JinaRankings; Assert.Equal(expected["results"]!.AsArray().Count, items.Count); Assert.NotEqual(0, items[0].Index);
        for (var index = 0; index < items.Count; index++)
        {
            Assert.True(JsonNode.DeepEquals(expected["results"]![index], JsonNode.Parse(items[index].Data.GetRawText())));
            Assert.Equal(expected["results"]![index]!["index"]!.GetValue<int>(), items[index].Index);
            Assert.Equal(expected["results"]![index]!["relevance_score"]!.GetValue<double>(), items[index].RelevanceScore);
        }
        if (key == "listwise") { Assert.Equal(new double[] { 0, 1.5 }, items[0].Embedding); Assert.Equal("returned-version", result.Data.GetProperty("model").GetString()); }
        else Assert.Null(items[0].Embedding);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>top_n 和原生用量允许精确的大整数及指数表示；不截短、舍入、填零或改写报文。</summary>
    [Theory] [InlineData("1.0", "0")] [InlineData("1e0", "2147483648")]
    [InlineData("9999999999999999999999999", "1e100")] [InlineData("1e999", "1.00e3")]
    public async Task NativeIntegerRepresentations_ArePreserved(string top, string usage)
    {
        var source = JinaRerankFixtures.Request(); source.Parameters["top_n"] = JsonSerializer.Deserialize<JsonElement>(top);
        var one = top is "1.0" or "1e0";
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var wire = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token)); Assert.Equal(top, wire.RootElement.GetProperty("top_n").GetRawText());
            var suffix = one ? "" : ",{\"index\":0,\"relevance_score\":-2,\"document\":\"A😀\"}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"model\":\"returned\",\"usage\":{\"total_tokens\":" + usage
                + "},\"results\":[{\"index\":1.0,\"relevance_score\":2.50000000000000000001,\"document\":{\"text\":\"B\"}}" + suffix + "]}") };
        });
        var result = await JinaRerankFixtures.Adapter(handler).RerankAsync(source, TestContext.Current.CancellationToken);
        Assert.Equal(usage, result.Data.GetProperty("usage").GetProperty("total_tokens").GetRawText()); Assert.Equal(1, result.JinaRankings[0].Index);
        Assert.Equal("2.50000000000000000001", result.JinaRankings[0].Data.GetProperty("relevance_score").GetRawText());
    }

    /// <summary>省略选项不向上游注入默认值，原生默认返回文档仍由响应契约校验。</summary>
    [Fact]
    public async Task NativeDefaults_AreNotWrittenIntoRequest()
    {
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var wire = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            foreach (var field in new[] { "top_n", "return_documents", "return_embeddings", "max_doc_length", "stream" }) Assert.False(wire.RootElement.TryGetProperty(field, out _));
            return JinaRerankFixtures.Json(JinaRerankFixtures.Read("text"));
        });
        await JinaRerankFixtures.Adapter(handler).RerankAsync(JinaRerankFixtures.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>原生返回字符串可以原样关联图片输入的内容，不为响应猜造 ImageDoc 包装。</summary>
    [Fact]
    public async Task ReturnedImageString_KeepsNativeContainerAndOriginalIndex()
    {
        var raw = JinaRerankFixtures.Read("multimodal-image-query"); raw["results"]![1]!["document"] = "https://media.invalid/candidate.png";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(JinaRerankFixtures.Json(raw)));
        var result = await JinaRerankFixtures.Adapter(handler).RerankAsync(JinaRerankFixtures.Request("multimodal-image-query"), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.JinaRankings[1].Index); Assert.Equal(JsonValueKind.String, result.JinaRankings[1].Document!.Value.ValueKind);
        Assert.Equal("https://media.invalid/candidate.png", result.JinaRankings[1].Document!.Value.GetString()); Assert.Equal(1, handler.Calls);
    }

    /// <summary>大小写不同的 model 扩展不覆盖公共路由，也不通过字典比较器丢失未知字段。</summary>
    [Fact]
    public async Task NativeExtensions_KeepExactRootKeys()
    {
        var request = JinaRerankFixtures.Request(); request = request with { Parameters = new(request.Parameters, StringComparer.OrdinalIgnoreCase) };
        request.Parameters["MODEL"] = JsonSerializer.SerializeToElement("extension");
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var wire = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal("native-rerank", wire.RootElement.GetProperty("model").GetString()); Assert.Equal("extension", wire.RootElement.GetProperty("MODEL").GetString());
            return JinaRerankFixtures.Json(JinaRerankFixtures.Read("text"));
        });
        await JinaRerankFixtures.Adapter(handler).RerankAsync(request, TestContext.Current.CancellationToken);
    }
}
