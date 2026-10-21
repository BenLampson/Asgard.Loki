using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Embedding.Ollama.Tests;

/// <summary>通过公共 SDK 验证原生多输入、无损参数、顺序向量和完整目标元数据。</summary>
public sealed class OllamaEmbeddingTests
{
    /// <summary>完整金样经分片 JSON HTTP，不改写模型 tag、输入、默认设置、返回数组或未知字段。</summary>
    [Theory] [MemberData(nameof(OllamaEmbeddingFixtures.Cases), MemberType = typeof(OllamaEmbeddingFixtures))]
    public async Task NativePipeline_MatchesIndependentGolden(string name)
    {
        var raw = OllamaEmbeddingFixtures.Read(name);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://ollama.invalid/tenant/api/embed", message.RequestUri!.AbsoluteUri); Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Null(message.Headers.Authorization); Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("application/json", Assert.Single(message.Headers.Accept).MediaType);
            Assert.True(JsonNode.DeepEquals(OllamaEmbeddingFixtures.Read(name, "wire"), JsonNode.Parse(await message.Content.ReadAsStringAsync(token))));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(raw.ToJsonString())) };
            response.Content.Headers.ContentType = new("application/json"); return response;
        });
        await using var client = OllamaEmbeddingFixtures.Build(OllamaEmbeddingFixtures.Adapter(handler));
        var request = OllamaEmbeddingFixtures.Request(name); var before = JsonSerializer.Serialize(request);
        var result = await client.Embedding.EmbedAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.True(JsonNode.DeepEquals(raw, JsonNode.Parse(result.Data.GetRawText())));
        Assert.Equal("public", result.Metadata.PublicModel); Assert.Equal("team/embed-test:tag", result.Metadata.UpstreamModel); Assert.Equal("ollama-vector", result.Metadata.ProviderId);
        var vectors = result.OllamaEmbeddings; Assert.Equal(raw["embeddings"]!.AsArray().Count, vectors.Count);
        for (var i = 0; i < vectors.Count; i++)
        {
            Assert.True(JsonNode.DeepEquals(raw["embeddings"]![i], JsonNode.Parse(vectors[i].Data.GetRawText())));
            Assert.Equal(raw["embeddings"]![i]!.AsArray().Select(v => v!.GetValue<double>()), vectors[i].Values);
        }
        Assert.False(result.Data.TryGetProperty("data", out _)); Assert.False(result.Data.TryGetProperty("usage", out _)); Assert.Equal(1, handler.Calls);
    }

    /// <summary>缺省、null、零项与批内空字符串有不同的原生对应数量，不隐式把一项丢弃。</summary>
    [Theory] [InlineData("{}", 0)] [InlineData("{\"input\":null}", 0)] [InlineData("{\"input\":[]}", 0)]
    [InlineData("{\"input\":\"\"}", 0)] [InlineData("{\"input\":[\"\"]}", 1)] [InlineData("{\"input\":[\"\",\"\"]}", 2)]
    public async Task EmptyNativeInputs_PreserveTheirDistinctShapes(string json, int count)
    {
        var parameters = JsonNode.Parse(json)!.AsObject(); var raw = OllamaEmbeddingFixtures.Read("empty");
        for (var i = 0; i < count; i++) raw["embeddings"]!.AsArray().Add(new JsonArray(0.1, 0.2));
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var expected = parameters.DeepClone().AsObject(); expected["model"] = "team/embed-test:tag";
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return OllamaEmbeddingFixtures.Json(raw);
        });
        var result = await OllamaEmbeddingFixtures.Adapter(handler).EmbedAsync(OllamaEmbeddingFixtures.Request(parameters), TestContext.Current.CancellationToken);
        Assert.Equal(count, result.OllamaEmbeddings.Count); Assert.Equal(1, handler.Calls);
    }

    /// <summary>未知嵌套字段保持原样，媒体字节只校验 Base64，不被解析、下载或改成其他模态。</summary>
    [Theory] [InlineData("AQID")] [InlineData("AQ\r\nID")] [InlineData("")]
    public async Task MediaAndFutureFields_AreNotRewritten(string media)
    {
        var parameters = JsonNode.Parse("""{"input":{"text":" ","future":false},"dimensions":0,"options":{"num_ctx":0,"future":null}}""")!.AsObject();
        parameters["input"]!["image"] = media; parameters["input"]!["audio"] = "BAUG";
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var expected = parameters.DeepClone().AsObject(); expected["model"] = "team/embed-test:tag";
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsStringAsync(token)))); return OllamaEmbeddingFixtures.Json(OllamaEmbeddingFixtures.Read());
        });
        var result = await OllamaEmbeddingFixtures.Adapter(handler).EmbedAsync(OllamaEmbeddingFixtures.Request(parameters), TestContext.Current.CancellationToken);
        Assert.Equal(new double[] { 0.1, -0.2, 0.3 }, result.OllamaEmbeddings.Single().Values); Assert.Equal(1, handler.Calls);
    }

    /// <summary>零/负维度按原生不缩减设置传递，时长字符串和秒数不由 SDK 解析或换算。</summary>
    [Theory] [InlineData("null", "null")] [InlineData("0", "0")] [InlineData("-1", "-1")]
    [InlineData("3", "\"1h30m\"")] [InlineData("0", "0.5")] [InlineData("0", "\"-1m\"")]
    public async Task NativeSettings_AreSentWithoutDefaultInjection(string dimensions, string keepAlive)
    {
        var parameters = JsonNode.Parse("{\"input\":\"x\",\"dimensions\":" + dimensions + ",\"keep_alive\":" + keepAlive + "}")!.AsObject();
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var expected = parameters.DeepClone().AsObject(); expected["model"] = "team/embed-test:tag";
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsStringAsync(token)))); return OllamaEmbeddingFixtures.Json(OllamaEmbeddingFixtures.Read());
        });
        var result = await OllamaEmbeddingFixtures.Adapter(handler).EmbedAsync(OllamaEmbeddingFixtures.Request(parameters), TestContext.Current.CancellationToken);
        Assert.Equal(3, result.OllamaEmbeddings.Single().Values.Count);
    }

    /// <summary>返回模型名可为服务端规范名，不改写配置名或原始响应，也不声称文本质量已验证。</summary>
    [Fact]
    public async Task CanonicalReturnedModelAndLargeCounters_AreRetained()
    {
        var raw = OllamaEmbeddingFixtures.Read(); raw["model"] = "team/embed-test:canonical";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(OllamaEmbeddingFixtures.Json(raw)));
        var result = await OllamaEmbeddingFixtures.Adapter(handler).EmbedAsync(OllamaEmbeddingFixtures.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(9007199254740993L, result.Data.GetProperty("total_duration").GetInt64());
        Assert.Equal("team/embed-test:canonical", result.Data.GetProperty("model").GetString()); Assert.Equal("team/embed-test:tag", result.Metadata.UpstreamModel);
        var vectors = result.OllamaEmbeddings; ((double[])vectors[0].Values)[0] = 99;
        Assert.Equal(0.1, vectors[0].Values[0]);
    }
}
