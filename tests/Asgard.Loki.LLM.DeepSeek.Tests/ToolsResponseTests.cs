using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.DeepSeek.Tests;

/// <summary>完整原生响应结构、空结果、分数和错误保真；不把响应变为应用策略。</summary>
public sealed class ToolsResponseTests
{
    /// <summary>所有 HTTP 错误状态和 HTTP200 原生错误完整保留，无重试或重定向。</summary>
    [Theory] [InlineData(false, 200)] [InlineData(false, 400)] [InlineData(false, 429)] [InlineData(false, 503)]
    [InlineData(true, 200)] [InlineData(true, 400)] [InlineData(true, 429)] [InlineData(true, 503)]
    public async Task NativeErrorKeepsBodyStatusAndType(bool fetch, int status)
    {
        var raw = ToolsFixture.Part("native_error").GetRawText(); using var factory = ToolsFixture.Factory(fetch); factory.OnSend = (_, _) =>
        { var reply = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(raw, Encoding.UTF8, "text/plain") }; reply.Headers.TryAddWithoutValidation("Retry-After", "1"); return Task.FromResult(reply); };
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => ToolsFixture.Invoke(new(factory, ToolsFixture.Options()), fetch, TestContext.Current.CancellationToken)); Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.Equal(Encoding.UTF8.GetBytes(raw), error.Body.ToArray()); Assert.Equal(raw, error.ResponseBody); Assert.Equal("text/plain; charset=utf-8", error.ContentType); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>二进制原生 HTTP 失败不经过有损解码，无法解析的类型头也完整保留。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task BinaryHttpErrorKeepsOriginalBytes(bool malformed)
    {
        byte[] bytes = [255, 0, 254]; using var factory = ToolsFixture.Factory(); factory.OnSend = (_, _) => { var content = new ByteArrayContent(bytes); content.Headers.TryAddWithoutValidation("Content-Type", malformed ? "not a media type" : "application/octet-stream"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = content }); };
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => ToolsFixture.Invoke(new(factory, ToolsFixture.Options()), false, TestContext.Current.CancellationToken)); Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal(malformed ? "not a media type" : "application/octet-stream", error.ContentType); Assert.Throws<DecoderFallbackException>(() => error.ResponseBody); Assert.Equal(1, factory.Calls);
    }
    /// <summary>搜索核心条目不能缺失或以错误类型代替；未知字段不会补造有效数据。</summary>
    [Theory] [InlineData("results")] [InlineData("item")] [InlineData("title")] [InlineData("url")] [InlineData("snippet")]
    [InlineData("summary")] [InlineData("content")] [InlineData("rerank_score")] [InlineData("authority")]
    [InlineData("site_name")] [InlineData("site_icon")] [InlineData("date")]
    public async Task InvalidSearchShapeCannotPublish(string field)
    {
        var data = JsonNode.Parse(ToolsFixture.Response().GetRawText())!.AsObject(); if (field == "results") data["results"] = new JsonObject(); else if (field == "item") data["results"]![0] = false; else data["results"]![0]![field] = false;
        using var factory = ToolsFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(ToolsFixture.Json(ToolsFixture.Element(data.ToJsonString())));
        await Assert.ThrowsAsync<InvalidDataException>(() => ToolsFixture.Invoke(new(factory, ToolsFixture.Options()), false, TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>非法/缺失抓取文本以及非对象完整响应不能被当作有效页面内容。</summary>
    [Theory] [InlineData("null")] [InlineData("[]")] [InlineData("{}")] [InlineData("{\"title\":null,\"content\":\"x\"}")]
    [InlineData("{\"title\":\"x\",\"content\":false}")]
    public async Task InvalidFetchShapeCannotPublish(string raw)
    {
        using var factory = ToolsFixture.Factory(true); factory.OnSend = (_, _) => Task.FromResult(ToolsFixture.Json(ToolsFixture.Element(raw)));
        await Assert.ThrowsAsync<InvalidDataException>(() => ToolsFixture.Invoke(new(factory, ToolsFixture.Options()), true, TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>原生评分和权威级别属于数据，SDK 不按当前质量阈值过滤或夹取未来值。</summary>
    [Theory] [InlineData(-0.25, -5)] [InlineData(3.75, 7)]
    public async Task NativeScoresAndTiersAreNotClamped(double score, int tier)
    {
        var data = JsonNode.Parse(ToolsFixture.Response().GetRawText())!.AsObject(); data["results"]![0]!["rerank_score"] = score; data["results"]![0]!["authority"] = tier; using var factory = ToolsFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(ToolsFixture.Json(ToolsFixture.Element(data.ToJsonString())));
        var result = await ToolsFixture.Invoke(new(factory, ToolsFixture.Options()), false, TestContext.Current.CancellationToken); Assert.True(JsonElement.DeepEquals(ToolsFixture.Element(data.ToJsonString()), result.Data)); Assert.Equal(2, result.Data.GetProperty("results").GetArrayLength());
    }
    /// <summary>可空元数据省略时保持省略，不填 null，也不丢弃其他原生字段。</summary>
    [Fact]
    public async Task MissingNullableMetadataStaysMissing()
    {
        var data = JsonNode.Parse(ToolsFixture.Response().GetRawText())!.AsObject(); var item = data["results"]![0]!.AsObject(); foreach (var name in new[] { "site_name", "site_icon", "date" }) item.Remove(name); using var factory = ToolsFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(ToolsFixture.Json(ToolsFixture.Element(data.ToJsonString())));
        var result = await ToolsFixture.Invoke(new(factory, ToolsFixture.Options()), false, TestContext.Current.CancellationToken); Assert.True(JsonElement.DeepEquals(ToolsFixture.Element(data.ToJsonString()), result.Data)); Assert.False(result.Data.GetProperty("results")[0].TryGetProperty("date", out _));
    }
    /// <summary>实际成功 HTTP 状态和带引号/自定义 JSON 媒体类型不被改写，完整结果仍有效。</summary>
    [Fact]
    public async Task ActualHttpMetadataIsPreserved()
    {
        using var factory = ToolsFixture.Factory(true); const string type = "application/vnd.deepseek+json; charset=\"UTF-8\""; factory.OnSend = (_, _) => { var content = new ByteArrayContent(Encoding.UTF8.GetBytes(ToolsFixture.Response(true).GetRawText())); content.Headers.TryAddWithoutValidation("Content-Type", type); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = content }); };
        var result = await ToolsFixture.Invoke(new(factory, ToolsFixture.Options()), true, TestContext.Current.CancellationToken); Assert.Equal(HttpStatusCode.Created, result.StatusCode); Assert.Equal(type, result.ContentType); Assert.True(JsonElement.DeepEquals(ToolsFixture.Response(true), result.Data));
    }
    /// <summary>合法 JSON 数字不能在平台解码后变成无限分数并被当作有效评分。</summary>
    [Fact]
    public async Task InfiniteNativeScoreCannotPublish()
    {
        var raw = ToolsFixture.Response().GetRawText().Replace("\"rerank_score\":0", "\"rerank_score\":1e400", StringComparison.Ordinal); using var factory = ToolsFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(ToolsFixture.Json(ToolsFixture.Element(raw)));
        await Assert.ThrowsAsync<InvalidDataException>(() => ToolsFixture.Invoke(new(factory, ToolsFixture.Options()), false, TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }

}
