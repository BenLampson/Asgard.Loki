using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Embedding.Google.Tests;

/// <summary>通过公共 SDK 验证原生操作、配置外壳、多模态传输与顺序对应。</summary>
public sealed class GoogleEmbeddingTests
{
    /// <summary>完整独立 wire 金样，批内任务/标题/维度独立，多模态原样传输而不进行解析或下载。</summary>
    [Theory] [MemberData(nameof(GoogleEmbeddingFixtures.Cases), MemberType = typeof(GoogleEmbeddingFixtures))]
    public async Task NativePipeline_MatchesIndependentWireGolden(string name)
    {
        var operation = GoogleEmbeddingFixtures.Operation(name); var raw = GoogleEmbeddingFixtures.Read(name + "-response");
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var method = operation == GoogleEmbeddingOperation.EmbedContent ? "embedContent" : "batchEmbedContents";
            Assert.Equal("https://google.invalid/tenant/v1beta/models/embedding-test:" + method, message.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("google-embedding-key", Assert.Single(message.Headers.GetValues("x-goog-api-key")));
            Assert.Null(message.Headers.Authorization); Assert.Equal("", message.RequestUri.Query);
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            Assert.True(JsonNode.DeepEquals(GoogleEmbeddingFixtures.Read(name + "-wire"), JsonNode.Parse(await message.Content.ReadAsStringAsync(token))));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(raw.ToJsonString())) };
        });
        await using var client = GoogleEmbeddingFixtures.Build(GoogleEmbeddingFixtures.Adapter(handler, operation));
        var request = GoogleEmbeddingFixtures.Request(name); var before = JsonSerializer.Serialize(request);
        var result = await client.Embedding.EmbedAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.True(JsonNode.DeepEquals(raw, JsonNode.Parse(result.Data.GetRawText())));
        Assert.Equal("public", result.Metadata.PublicModel); Assert.Equal("embedding-test", result.Metadata.UpstreamModel); Assert.Equal("google-vector", result.Metadata.ProviderId);
        var vectors = result.GoogleEmbeddings; Assert.Equal(operation == GoogleEmbeddingOperation.EmbedContent ? 1 : 2, vectors.Count);
        Assert.Equal(new double[] { 0.1, 0.2 }, vectors[0].Values);
        if (vectors.Count == 2) Assert.Equal(new double[] { 0.3 }, vectors[1].Values);
        Assert.All(vectors, v => Assert.False(v.Data.TryGetProperty("index", out _))); Assert.Equal(1, handler.Calls);
    }

    /// <summary>ProtoJSON null/unset 不改成默认值，缺失用量保持未知，数值字符串的原文仍可访问。</summary>
    [Fact]
    public async Task ProtoJsonNullAndQuotedNumbers_ArePreserved()
    {
        var source = GoogleEmbeddingFixtures.Request();
        source.Parameters["outputDimensionality"] = JsonSerializer.SerializeToElement<object?>(null);
        source.Parameters["embedContentConfig"] = JsonSerializer.SerializeToElement(new { taskType = "FUTURE_TASK", outputDimensionality = (int?)null, autoTruncate = false });
        var raw = GoogleEmbeddingFixtures.Read("single-config-response"); raw.Remove("usageMetadata"); raw["embedding"]!["values"] = new JsonArray("0.1", "2e-1");
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var body = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!;
            Assert.Null(body["outputDimensionality"]); Assert.Null(body["embedContentConfig"]!["outputDimensionality"]);
            Assert.Equal("FUTURE_TASK", body["embedContentConfig"]!["taskType"]!.GetValue<string>());
            Assert.False(body.AsObject().ContainsKey("stream")); return GoogleEmbeddingFixtures.Json(raw);
        });
        var result = await GoogleEmbeddingFixtures.Adapter(handler).EmbedAsync(source, TestContext.Current.CancellationToken);
        Assert.False(result.Data.TryGetProperty("usageMetadata", out _)); Assert.Equal(new double[] { 0.1, 0.2 }, result.GoogleEmbeddings[0].Values);
        Assert.Equal(JsonValueKind.String, result.Data.GetProperty("embedding").GetProperty("values")[0].ValueKind);
    }

    /// <summary>原生 int32 的字符串/指数表示保持不变，校验结果长度时不使用浮点舍入近似。</summary>
    [Theory] [InlineData("2")] [InlineData("\"2\"")] [InlineData("2e0")] [InlineData("\"2e0\"")] [InlineData("2.00")] [InlineData("\"+002\"")]
    public async Task ExactDimensionRepresentations_AreNotRewritten(string value)
    {
        var source = GoogleEmbeddingFixtures.Request("single-legacy");
        using var number = JsonDocument.Parse(value); source.Parameters["outputDimensionality"] = number.RootElement.Clone();
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            var actual = body.RootElement.GetProperty("outputDimensionality");
            Assert.Equal(number.RootElement.ValueKind, actual.ValueKind);
            if (actual.ValueKind == JsonValueKind.String) Assert.Equal(number.RootElement.GetString(), actual.GetString());
            else Assert.Equal(number.RootElement.GetRawText(), actual.GetRawText());
            return GoogleEmbeddingFixtures.Json(GoogleEmbeddingFixtures.Read("single-legacy-response"));
        });
        await GoogleEmbeddingFixtures.Adapter(handler).EmbedAsync(source, TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }

    /// <summary>相同的新旧明确维度可共存，SDK 不折叠原生配置或重写其他设置。</summary>
    [Fact]
    public async Task MatchingDimensions_InBothEnvelopesRemainPresent()
    {
        var source = GoogleEmbeddingFixtures.Request(); source.Parameters["outputDimensionality"] = JsonSerializer.SerializeToElement(2);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var body = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!;
            Assert.Equal(2, body["outputDimensionality"]!.GetValue<int>()); Assert.Equal("2", body["embedContentConfig"]!["outputDimensionality"]!.GetValue<string>());
            return GoogleEmbeddingFixtures.Json(GoogleEmbeddingFixtures.Read("single-config-response"));
        });
        await GoogleEmbeddingFixtures.Adapter(handler).EmbedAsync(source, TestContext.Current.CancellationToken);
    }
}
