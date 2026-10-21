using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Embedding.Jina.Tests;

/// <summary>经公共 SDK 比较原生完整报文、输入索引与 token 矩阵，不 flatten 或解释媒体。</summary>
public sealed class JinaEmbeddingWireTests
{
    /// <summary>所有原生参数、未知字段、返回 item 顺序和原始 JSON 独立保留。</summary>
    [Theory] [MemberData(nameof(JinaEmbeddingFixtures.Cases), MemberType = typeof(JinaEmbeddingFixtures))]
    public async Task NativePipeline_MatchesIndependentGolden(string name)
    {
        var raw = JinaEmbeddingFixtures.Read(name);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://jina.invalid/tenant/v1/embeddings", message.RequestUri!.AbsoluteUri); Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("jina-test-key", message.Headers.Authorization.Parameter);
            Assert.False(message.Headers.Contains("OpenAI-Organization")); Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            Assert.True(JsonNode.DeepEquals(JinaEmbeddingFixtures.Read(name, "wire"), JsonNode.Parse(await message.Content.ReadAsStringAsync(token))));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(raw.ToJsonString())) };
            response.Content.Headers.ContentType = new("application/json"); return response;
        });
        await using var client = JinaEmbeddingFixtures.Build(JinaEmbeddingFixtures.Adapter(handler)); var request = JinaEmbeddingFixtures.Request(name);
        var before = JsonSerializer.Serialize(request); var result = await client.Embedding.EmbedAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.True(JsonNode.DeepEquals(raw, JsonNode.Parse(result.Data.GetRawText())));
        Assert.Equal("public", result.Metadata.PublicModel); Assert.Equal("embedding-native", result.Metadata.UpstreamModel); Assert.Equal("jina-vector", result.Metadata.ProviderId);
        var expected = raw["data"]!.AsArray(); var items = result.JinaEmbeddings; Assert.Equal(expected.Count, items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            Assert.Equal(expected[i]!["index"]!.GetValue<int>(), items[i].Index);
            Assert.Equal(expected[i]!.AsObject().ContainsKey("embeddings"), items[i].IsMultiVector);
            Assert.True(JsonNode.DeepEquals(expected[i], JsonNode.Parse(items[i].Data.GetRawText())));
            if (items[i].IsMultiVector)
            {
                Assert.Null(items[i].Embedding); Assert.Null(items[i].Values); Assert.Null(items[i].Base64);
                Assert.True(JsonNode.DeepEquals(expected[i]!["embeddings"], JsonNode.Parse(items[i].Embeddings!.Value.GetRawText())));
                Assert.Throws<InvalidOperationException>(() => items[i].DecodeBytes());
                if (expected[i]!["tokenized_input"] is JsonArray tokens) Assert.Equal(tokens.Select(t => t!.GetValue<string>()), items[i].TokenizedInput);
                else Assert.Null(items[i].TokenizedInput);
            }
            else
            {
                Assert.Null(items[i].Embeddings); Assert.Null(items[i].TokenizedInput);
                if (items[i].Base64 is { } encoded) Assert.Equal(Convert.FromBase64String(encoded), items[i].DecodeBytes().ToArray());
                else Assert.Equal(expected[i]!["embedding"]!.AsArray().Select(v => v!.GetValue<double>()), items[i].Values);
            }
        }
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>原生允许的空字符串、空批次与零 token 矩阵保持不同，均由调用者明确发起 HTTP。</summary>
    [Theory] [InlineData("string")] [InlineData("object")] [InlineData("batch")] [InlineData("multi")]
    public async Task EmptyNativeShapes_AreNotInventedOrCollapsed(string kind)
    {
        var request = JinaEmbeddingFixtures.Request("empty-list"); var raw = JinaEmbeddingFixtures.Read("empty-list");
        if (kind != "batch")
        {
            request.Parameters["input"] = kind == "object" ? JsonSerializer.SerializeToElement(new { text = "" }) : JsonSerializer.SerializeToElement("");
            raw["data"]!.AsArray().Add(kind == "multi" ? JsonNode.Parse("""{"index":0,"embeddings":[],"tokenized_input":[]}""") : JsonNode.Parse("""{"index":0,"embedding":[0.1,0.2]}"""));
            if (kind == "multi")
            { request.Parameters["return_multivector"] = JsonSerializer.SerializeToElement(true); request.Parameters["return_tokenized_input"] = JsonSerializer.SerializeToElement(true); }
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(JinaEmbeddingFixtures.Json(raw)));
        var result = await JinaEmbeddingFixtures.Adapter(handler).EmbedAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(kind == "batch" ? 0 : 1, result.JinaEmbeddings.Count); Assert.Equal(1, handler.Calls);
    }

    /// <summary>显式 int32 的等值整数表示与较大原生计数不通过 double 舍入，也不改写数字文本。</summary>
    [Theory] [InlineData("2")] [InlineData("2e0")] [InlineData("2.00")]
    public async Task ExactDimensionsAndCounters_AreRetained(string dimensions)
    {
        var request = JinaEmbeddingFixtures.Request(); request.Parameters["dimensions"] = JsonSerializer.SerializeToElement(JsonNode.Parse(dimensions));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal(dimensions, body.RootElement.GetProperty("dimensions").GetRawText()); return JinaEmbeddingFixtures.Json(JinaEmbeddingFixtures.Read());
        });
        var result = await JinaEmbeddingFixtures.Adapter(handler).EmbedAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(9007199254740993L, result.Data.GetProperty("usage").GetProperty("total_tokens").GetInt64());
    }

    /// <summary>服务端规范模型名与 item/返回文档释放独立；便利数组不能反向修改原始 JSON。</summary>
    [Fact]
    public async Task ReturnedIdentityAndViews_KeepIndependentRawData()
    {
        var raw = JinaEmbeddingFixtures.Read("multi"); raw["model"] = "native-canonical";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(JinaEmbeddingFixtures.Json(raw)));
        var result = await JinaEmbeddingFixtures.Adapter(handler).EmbedAsync(JinaEmbeddingFixtures.Request("multi"), TestContext.Current.CancellationToken);
        Assert.Equal("native-canonical", result.Data.GetProperty("model").GetString()); Assert.Equal("embedding-native", result.Metadata.UpstreamModel);
        var item = result.JinaEmbeddings[0]; ((string[])item.TokenizedInput!)[0] = "changed"; Assert.Equal("e", item.TokenizedInput![0]);
        using var document = JsonDocument.Parse("""{"index":0,"embedding":[0.1,0.2]}"""); var single = new JinaEmbeddingItem(document.RootElement);
        document.Dispose(); ((double[])single.Values!)[0] = 99; Assert.Equal(0.1, single.Values![0]);
    }
}
