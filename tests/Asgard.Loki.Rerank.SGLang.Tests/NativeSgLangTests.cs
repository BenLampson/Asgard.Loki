using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Rerank.SGLang.Tests;

/// <summary>公共 SDK 的原生 SGLang 完整请求、数组结果和文档/索引关联。</summary>
public sealed class NativeSgLangTests
{
    /// <summary>原生文本和内容块、top_n 与可选元信息保留，不注入 model 或改名分数。</summary>
    [Theory]
    [InlineData(0, true)] [InlineData(0, false)] [InlineData(1, true)] [InlineData(1, false)] [InlineData(2, true)] [InlineData(2, false)]
    public async Task FullNativeWireAndArrayRemainOriginal(int index, bool bearer)
    {
        var options = SgLangFixture.Options(bearer ? SgLangRerankAuthentication.BearerToken : SgLangRerankAuthentication.Anonymous);
        using var factory = SgLangFixture.Factory(index); using var stream = new SgLangReadStream(Encoding.UTF8.GetBytes(SgLangFixture.Part("responses")[index].GetRawText()));
        factory.OnSend = async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://rerank.example.invalid/proxy/v1/rerank", message.RequestUri!.AbsoluteUri);
            if (bearer) { Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(options.ApiKey, message.Headers.Authorization.Parameter); } else Assert.Null(message.Headers.Authorization);
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType); Assert.Equal("application/json", Assert.Single(message.Headers.Accept).MediaType);
            var wire = SgLangFixture.Element(await message.Content.ReadAsStringAsync(token)); Assert.True(JsonElement.DeepEquals(SgLangFixture.Part("requests")[index], wire));
            Assert.False(wire.TryGetProperty("model", out _)); Assert.False(wire.TryGetProperty("stream", out _));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        };
        await using var client = new LokiClientBuilder().AddRerank(new() { Routes = [new() { Model = "public-ranker", Targets = [new SgLangRerankAdapter(factory, options)] }] }).Build();
        var request = SgLangFixture.Request(index); var before = JsonSerializer.Serialize(request); var result = await client.Rerank.RerankAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.True(JsonElement.DeepEquals(SgLangFixture.Part("responses")[index], result.Data)); Assert.Equal(JsonValueKind.Array, result.Data.ValueKind);
        Assert.Equal(new RerankResultMetadata(request.Model, options.Model, options.Id), result.Metadata); Assert.Equal(result.Data.GetArrayLength(), result.SgLangRankings.Count);
        Assert.Equal(index == 0 ? 2 : 1, result.SgLangRankings[0].Index); Assert.True(stream.Eof); Assert.True(stream.Disposed); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
        Assert.Equal(options.HttpClientName, factory.ClientName);
        if (index == 0)
        {
            Assert.Equal("4.123456789012345678901", result.SgLangRankings[0].Data.GetProperty("score").GetRawText()); Assert.True(result.SgLangRankings[0].Score > 1); Assert.True(result.SgLangRankings[1].Score < 0);
            Assert.Equal(" Third  ", result.SgLangRankings[0].Document!.Value.GetString()); Assert.Equal("9007199254740993", result.SgLangRankings[0].MetaInfo!.Value.GetProperty("future").GetRawText());
        }
        if (index == 1) { Assert.Null(result.SgLangRankings[0].MetaInfo); Assert.Equal(JsonValueKind.Null, result.SgLangRankings[1].MetaInfo!.Value.ValueKind); Assert.Null(result.SgLangRankings[0].Document); }
    }
    /// <summary>请求在借用工厂前独立快照；工厂不得改变已选模型身份或原文档。</summary>
    [Fact]
    public async Task SnapshotPrecedesFactoryAndPreservesUnknownJson()
    {
        var request = SgLangFixture.Request(); using var factory = SgLangFixture.Factory(); factory.OnCreate = request.Parameters.Clear;
        factory.OnSend = async (message, token) => { Assert.True(JsonElement.DeepEquals(SgLangFixture.Part("requests")[0], SgLangFixture.Element(await message.Content!.ReadAsStringAsync(token)))); return SgLangFixture.Json(SgLangFixture.Part("responses")[0]); };
        await new SgLangRerankAdapter(factory, SgLangFixture.Options()).RerankAsync(request, TestContext.Current.CancellationToken); Assert.Empty(request.Parameters); Assert.Equal(1, factory.Calls);
    }
    /// <summary>省略原生可选参数时不注入默认值；服务器默认文档回显仍验证原文关联。</summary>
    [Fact]
    public async Task OmittedNativeOptionsRemainAbsent()
    {
        var request = SgLangFixture.Request(1); request.Parameters.Remove("top_n"); request.Parameters.Remove("return_documents"); request.Parameters.Remove("instruct");
        using var factory = SgLangFixture.Factory(); factory.OnSend = async (message, token) =>
        {
            var body = SgLangFixture.Element(await message.Content!.ReadAsStringAsync(token)); Assert.False(body.TryGetProperty("top_n", out _)); Assert.False(body.TryGetProperty("return_documents", out _)); Assert.False(body.TryGetProperty("instruct", out _));
            return SgLangFixture.Json(SgLangFixture.Element("""[{"index":1,"score":1,"document":"Second"},{"index":0,"score":0,"document":"First"}]"""));
        };
        await new SgLangRerankAdapter(factory, SgLangFixture.Options()).RerankAsync(request, TestContext.Current.CancellationToken);
    }
    /// <summary>超出文档数的精确 top_n 不溢出、不按其值分配、不改变原始 JSON。</summary>
    [Theory] [InlineData("9007199254740993")] [InlineData("1e1000000")]
    public async Task LargeTopNIsRetainedAndCapsOnlyExpectedCount(string raw)
    {
        var request = SgLangFixture.Request(1); request.Parameters["top_n"] = SgLangFixture.Element(raw); using var factory = SgLangFixture.Factory(1);
        factory.OnSend = async (message, token) => { Assert.Equal(raw, SgLangFixture.Element(await message.Content!.ReadAsStringAsync(token)).GetProperty("top_n").GetRawText()); return SgLangFixture.Json(SgLangFixture.Part("responses")[1]); };
        var result = await new SgLangRerankAdapter(factory, SgLangFixture.Options()).RerankAsync(request, TestContext.Current.CancellationToken); Assert.Equal(2, result.Data.GetArrayLength());
    }
    /// <summary>HTTP 或原生业务错误保留完整状态/原文/类型，不成为空数组也不重试。</summary>
    [Theory] [InlineData(400, false)] [InlineData(429, false)] [InlineData(503, false)] [InlineData(200, false)] [InlineData(200, true)]
    public async Task NativeErrorsRemainWhole(int status, bool envelope)
    {
        var raw = SgLangFixture.Part(envelope ? "enveloped_error" : "native_error"); using var factory = SgLangFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(SgLangFixture.Json(raw, (HttpStatusCode)status));
        var error = await Assert.ThrowsAsync<RerankUpstreamException>(() => new SgLangRerankAdapter(factory, SgLangFixture.Options()).RerankAsync(SgLangFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.Equal(Encoding.UTF8.GetBytes(raw.GetRawText()), error.Body.ToArray()); Assert.Equal(raw.GetRawText(), error.ResponseBody); Assert.Equal("application/json; charset=utf-8", error.ContentType); Assert.Equal(1, factory.Calls);
    }
}
