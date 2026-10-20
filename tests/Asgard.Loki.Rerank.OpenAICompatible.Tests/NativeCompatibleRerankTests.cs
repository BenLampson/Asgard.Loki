using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Rerank.OpenAICompatible.Tests;

/// <summary>公共 SDK 的完整兼容协议、原生候选、分数/索引和可缺省身份/用量。</summary>
public sealed class NativeCompatibleRerankTests
{
    /// <summary>文本或原生 JSON 候选保持原边界；返回原生结果而非其他供应商的格式。</summary>
    [Theory]
    [InlineData(0, true)] [InlineData(0, false)] [InlineData(1, true)] [InlineData(1, false)] [InlineData(2, true)] [InlineData(2, false)]
    public async Task FullWireAndNativeResultRemainOriginal(int index, bool bearer)
    {
        var options = CompatibleRerankFixture.Options(bearer ? OpenAiCompatibleRerankAuthentication.BearerToken : OpenAiCompatibleRerankAuthentication.Anonymous);
        using var factory = CompatibleRerankFixture.Factory(index); using var stream = new CompatibleRerankReadStream(Encoding.UTF8.GetBytes(CompatibleRerankFixture.Part("responses")[index].GetRawText()));
        factory.OnSend = async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://rerank.example.invalid/proxy/v1/rerank", message.RequestUri!.AbsoluteUri);
            if (bearer) { Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(options.ApiKey, message.Headers.Authorization.Parameter); } else Assert.Null(message.Headers.Authorization);
            var wire = CompatibleRerankFixture.Element(await message.Content!.ReadAsStringAsync(token)); Assert.True(JsonElement.DeepEquals(CompatibleRerankFixture.Part("requests")[index], wire));
            Assert.Equal(options.Model, wire.GetProperty("model").GetString()); Assert.False(wire.TryGetProperty("stream", out _));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        };
        await using var client = new LokiClientBuilder().AddRerank(new() { Routes = [new() { Model = "public-ranker", Targets = [new OpenAiCompatibleRerankAdapter(factory, options)] }] }).Build();
        var request = CompatibleRerankFixture.Request(index); var before = JsonSerializer.Serialize(request); var result = await client.Rerank.RerankAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.True(JsonElement.DeepEquals(CompatibleRerankFixture.Part("responses")[index], result.Data)); Assert.Equal(JsonValueKind.Object, result.Data.ValueKind);
        Assert.Equal(new RerankResultMetadata(request.Model, options.Model, options.Id), result.Metadata); Assert.Equal(result.Data.GetProperty("results").GetArrayLength(), result.OpenAiCompatibleRankings.Count);
        Assert.Equal(index == 2 ? 1 : 2, result.OpenAiCompatibleRankings[0].Index); Assert.True(stream.Eof); Assert.True(stream.Disposed); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed); Assert.Equal(options.HttpClientName, factory.ClientName);
        if (index == 0)
        {
            Assert.Equal("4.123456789012345678901", result.OpenAiCompatibleRankings[0].Data.GetProperty("relevance_score").GetRawText()); Assert.True(result.OpenAiCompatibleRankings[0].RelevanceScore > 1); Assert.True(result.OpenAiCompatibleRankings[1].RelevanceScore < 0);
            Assert.Equal("9007199254740993", result.Data.GetProperty("usage").GetProperty("future").GetRawText()); Assert.Equal("native-reported-version", result.Data.GetProperty("model").GetString());
        }
        if (index == 1) { Assert.False(result.Data.TryGetProperty("usage", out _)); Assert.False(result.Data.TryGetProperty("model", out _)); Assert.Equal(JsonValueKind.Null, result.OpenAiCompatibleRankings[0].Document!.Value.ValueKind); }
        if (index == 2) { Assert.Equal(JsonValueKind.Null, result.Data.GetProperty("usage").ValueKind); Assert.Null(result.OpenAiCompatibleRankings[0].Document); }
    }
    /// <summary>工厂创建前完成请求快照；后续清空不改变完整原生请求。</summary>
    [Fact]
    public async Task SnapshotPrecedesFactory()
    {
        var request = CompatibleRerankFixture.Request(); using var factory = CompatibleRerankFixture.Factory(); factory.OnCreate = request.Parameters.Clear;
        factory.OnSend = async (message, token) => { Assert.True(JsonElement.DeepEquals(CompatibleRerankFixture.Part("requests")[0], CompatibleRerankFixture.Element(await message.Content!.ReadAsStringAsync(token)))); return CompatibleRerankFixture.Json(CompatibleRerankFixture.Part("responses")[0]); };
        await new OpenAiCompatibleRerankAdapter(factory, CompatibleRerankFixture.Options()).RerankAsync(request, TestContext.Current.CancellationToken); Assert.Empty(request.Parameters);
    }
    /// <summary>原生可选参数为空时保持空值，不插默认；无用量和无身份仍是已验证的最小响应。</summary>
    [Fact]
    public async Task NullableFlagsAndMissingMetadataStayNative()
    {
        var request = CompatibleRerankFixture.Request(1); request.Parameters["return_documents"] = CompatibleRerankFixture.Element("null"); using var factory = CompatibleRerankFixture.Factory(1);
        factory.OnSend = async (message, token) => { var body = CompatibleRerankFixture.Element(await message.Content!.ReadAsStringAsync(token)); Assert.Equal(JsonValueKind.Null, body.GetProperty("top_n").ValueKind); Assert.Equal(JsonValueKind.Null, body.GetProperty("return_documents").ValueKind); return CompatibleRerankFixture.Json(CompatibleRerankFixture.Part("responses")[1]); };
        var result = await new OpenAiCompatibleRerankAdapter(factory, CompatibleRerankFixture.Options()).RerankAsync(request, TestContext.Current.CancellationToken); Assert.False(result.Data.TryGetProperty("usage", out _));
    }
    /// <summary>原始空白/空文本仅按原生 JSON 传递，是否接受由模型服务决定，不擅自修剪或过滤。</summary>
    [Fact]
    public async Task EmptyAndWhitespaceNativeTextIsNotRewritten()
    {
        var request = CompatibleRerankFixture.Request(2); request.Parameters["query"] = JsonSerializer.SerializeToElement(""); request.Parameters["documents"] = JsonSerializer.SerializeToElement(new[] { "", "  " });
        using var factory = CompatibleRerankFixture.Factory(2); factory.OnSend = async (message, token) => { var body = CompatibleRerankFixture.Element(await message.Content!.ReadAsStringAsync(token)); Assert.Equal("", body.GetProperty("query").GetString()); Assert.Equal("", body.GetProperty("documents")[0].GetString()); Assert.Equal("  ", body.GetProperty("documents")[1].GetString()); Assert.False(body.TryGetProperty("top_n", out _)); Assert.False(body.TryGetProperty("return_documents", out _)); return CompatibleRerankFixture.Json(CompatibleRerankFixture.Part("responses")[2]); };
        await new OpenAiCompatibleRerankAdapter(factory, CompatibleRerankFixture.Options()).RerankAsync(request, TestContext.Current.CancellationToken);
    }
    /// <summary>巨大精确 top_n 不导致溢出、舍入或按值分配数组，返回数量只以输入数为上限。</summary>
    [Theory] [InlineData("9007199254740993")] [InlineData("1e1000000")]
    public async Task LargeTopNDoesNotRewriteNativeRequest(string raw)
    {
        var request = CompatibleRerankFixture.Request(2); request.Parameters["top_n"] = CompatibleRerankFixture.Element(raw); using var factory = CompatibleRerankFixture.Factory(2);
        factory.OnSend = async (message, token) => { Assert.Equal(raw, CompatibleRerankFixture.Element(await message.Content!.ReadAsStringAsync(token)).GetProperty("top_n").GetRawText()); return CompatibleRerankFixture.Json(CompatibleRerankFixture.Part("responses")[2]); };
        await new OpenAiCompatibleRerankAdapter(factory, CompatibleRerankFixture.Options()).RerankAsync(request, TestContext.Current.CancellationToken);
    }
    /// <summary>HTTP 和原生 HTTP200 错误保持原始字节、实际状态和类型，不自动重试。</summary>
    [Theory] [InlineData(400, false)] [InlineData(429, false)] [InlineData(503, false)] [InlineData(200, false)] [InlineData(200, true)]
    public async Task NativeErrorRetainsRawPayload(int status, bool detail)
    {
        var raw = CompatibleRerankFixture.Part(detail ? "detail_error" : "native_error"); using var factory = CompatibleRerankFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(CompatibleRerankFixture.Json(raw, (HttpStatusCode)status));
        var error = await Assert.ThrowsAsync<RerankUpstreamException>(() => new OpenAiCompatibleRerankAdapter(factory, CompatibleRerankFixture.Options()).RerankAsync(CompatibleRerankFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.Equal(Encoding.UTF8.GetBytes(raw.GetRawText()), error.Body.ToArray()); Assert.Equal("application/json; charset=utf-8", error.ContentType); Assert.Equal(1, factory.Calls);
    }
}
