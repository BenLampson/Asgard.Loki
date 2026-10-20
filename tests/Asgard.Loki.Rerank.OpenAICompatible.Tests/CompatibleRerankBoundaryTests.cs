using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Rerank.OpenAICompatible.Tests;

/// <summary>最小原生兼容协议边界，不将其他响应或错误关联补造成成功。</summary>
public sealed class CompatibleRerankBoundaryTests
{
    /// <summary>无效原生结构/选项在工厂前失败，未知有效 JSON 不作格式化处理。</summary>
    [Theory]
    [InlineData("query-missing")] [InlineData("query-array")] [InlineData("query-null")] [InlineData("documents-missing")] [InlineData("documents-scalar")] [InlineData("documents-empty")]
    [InlineData("top-zero")] [InlineData("top-negative")] [InlineData("top-fraction")] [InlineData("top-near-integer")] [InlineData("return-type")]
    [InlineData("model")] [InlineData("undefined")] [InlineData("duplicate-unknown")] [InlineData("stream")]
    public async Task InvalidRequestsFailBeforeHttpFactory(string kind)
    {
        var request = CompatibleRerankFixture.Request(); var p = request.Parameters;
        switch (kind)
        {
            case "query-missing": p.Remove("query"); break;
            case "query-array": p["query"] = CompatibleRerankFixture.Element("[]"); break;
            case "query-null": p["query"] = CompatibleRerankFixture.Element("null"); break;
            case "documents-missing": p.Remove("documents"); break;
            case "documents-scalar": p["documents"] = CompatibleRerankFixture.Element("\"wrong structure\""); break;
            case "documents-empty": p["documents"] = CompatibleRerankFixture.Element("[]"); break;
            case "top-zero": p["top_n"] = CompatibleRerankFixture.Element("0"); break;
            case "top-negative": p["top_n"] = CompatibleRerankFixture.Element("-1"); break;
            case "top-fraction": p["top_n"] = CompatibleRerankFixture.Element("1.5"); break;
            case "top-near-integer": p["top_n"] = CompatibleRerankFixture.Element("2.0000000000000000001"); break;
            case "return-type": p["return_documents"] = CompatibleRerankFixture.Element("1"); break;
            case "model": p["MODEL"] = CompatibleRerankFixture.Element("\"override\""); break;
            case "undefined": p["future"] = default; break;
            case "duplicate-unknown": p["future"] = CompatibleRerankFixture.Element("{\"same\":1,\"same\":2}"); break;
            case "stream": p["stream"] = CompatibleRerankFixture.Element("true"); break;
        }
        using var factory = CompatibleRerankFixture.Factory();
        if (kind == "duplicate-unknown") await Assert.ThrowsAnyAsync<JsonException>(() => new OpenAiCompatibleRerankAdapter(factory, CompatibleRerankFixture.Options()).RerankAsync(request, TestContext.Current.CancellationToken));
        else await Assert.ThrowsAnyAsync<ArgumentException>(() => new OpenAiCompatibleRerankAdapter(factory, CompatibleRerankFixture.Options()).RerankAsync(request, TestContext.Current.CancellationToken)); Assert.Equal(0, factory.Creates);
    }
    /// <summary>不完整数量、错误索引/分数、文档或已知用量不能通过缺省值修复。</summary>
    [Theory]
    [InlineData("score-array")] [InlineData("missing-results")] [InlineData("empty-results")] [InlineData("missing-item")] [InlineData("duplicate-index")] [InlineData("foreign-index")]
    [InlineData("negative-index")] [InlineData("fraction-index")] [InlineData("near-integer-index")] [InlineData("missing-index")] [InlineData("string-score")] [InlineData("missing-score")]
    [InlineData("overflow-score")] [InlineData("null-score")] [InlineData("score-alias")] [InlineData("wrong-document")] [InlineData("missing-document")] [InlineData("null-document")]
    [InlineData("usage-array")] [InlineData("usage-negative")] [InlineData("usage-fraction")]
    public async Task InvalidResultsCannotBecomeNativeRankings(string kind)
    {
        var body = JsonNode.Parse(CompatibleRerankFixture.Part("responses")[0].GetRawText())!.AsObject(); var results = body["results"]!.AsArray();
        switch (kind)
        {
            case "missing-results": body.Remove("results"); break;
            case "empty-results": results.Clear(); break;
            case "missing-item": results.RemoveAt(1); break;
            case "duplicate-index": results[1]!["index"] = 2; break;
            case "foreign-index": results[0]!["index"] = 3; break;
            case "negative-index": results[0]!["index"] = -1; break;
            case "fraction-index": results[0]!["index"] = 1.5; break;
            case "near-integer-index": results[0]!["index"] = JsonNode.Parse("1.999999999999999999999"); break;
            case "missing-index": results[0]!.AsObject().Remove("index"); break;
            case "string-score": results[0]!["relevance_score"] = "1"; break;
            case "missing-score": results[0]!.AsObject().Remove("relevance_score"); break;
            case "overflow-score": results[0]!["relevance_score"] = JsonNode.Parse("1e1000000"); break;
            case "null-score": results[0]!["relevance_score"] = null; break;
            case "score-alias": results[0]!.AsObject().Remove("relevance_score"); results[0]!["score"] = 1; break;
            case "wrong-document": results[0]!["document"] = "First original"; break;
            case "missing-document": results[0]!.AsObject().Remove("document"); break;
            case "null-document": results[0]!["document"] = null; break;
            case "usage-array": body["usage"] = new JsonArray(); break;
            case "usage-negative": body["usage"]!["total_tokens"] = -1; break;
            case "usage-fraction": body["usage"]!["total_tokens"] = JsonNode.Parse("0.0000000000000000000001"); break;
        }
        var raw = kind == "score-array" ? CompatibleRerankFixture.Part("foreign_score_array") : CompatibleRerankFixture.Element(body.ToJsonString());
        using var factory = CompatibleRerankFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(CompatibleRerankFixture.Json(raw));
        await Assert.ThrowsAsync<InvalidDataException>(() => new OpenAiCompatibleRerankAdapter(factory, CompatibleRerankFixture.Options()).RerankAsync(CompatibleRerankFixture.Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>原对象未知字段也是关联内容，不能只比较 text 丢弃其他原候选字段。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ObjectEchoMustRetainAllOriginalFields(bool missing)
    {
        var body = JsonNode.Parse(CompatibleRerankFixture.Part("responses")[1].GetRawText())!.AsObject(); var document = body["results"]![2]!["document"]!.AsObject();
        if (missing) document.Remove("future"); else document["future"] = true;
        using var factory = CompatibleRerankFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(CompatibleRerankFixture.Json(CompatibleRerankFixture.Element(body.ToJsonString())));
        await Assert.ThrowsAsync<InvalidDataException>(() => new OpenAiCompatibleRerankAdapter(factory, CompatibleRerankFixture.Options()).RerankAsync(CompatibleRerankFixture.Request(1), TestContext.Current.CancellationToken));
    }
    /// <summary>原生空候选的显式 null 回显和缺失字段不能混为一谈。</summary>
    [Fact]
    public async Task RequiredNullOriginalCandidateKeepsExplicitNullEcho()
    {
        var request = CompatibleRerankFixture.Request(1); request.Parameters["return_documents"] = CompatibleRerankFixture.Element("true"); using var factory = CompatibleRerankFixture.Factory(1);
        var result = await new OpenAiCompatibleRerankAdapter(factory, CompatibleRerankFixture.Options()).RerankAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Null, result.OpenAiCompatibleRankings[0].Document!.Value.ValueKind);
    }
    /// <summary>截断或非 JSON 不能变为完整结果。</summary>
    [Theory] [InlineData("not-json")] [InlineData("{\"results\":[")]
    public async Task MalformedJsonFailsBeforePublication(string text)
    {
        using var factory = CompatibleRerankFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") });
        await Assert.ThrowsAnyAsync<JsonException>(() => new OpenAiCompatibleRerankAdapter(factory, CompatibleRerankFixture.Options()).RerankAsync(CompatibleRerankFixture.Request(), TestContext.Current.CancellationToken));
    }
    /// <summary>SSE 成功响应在读取正文前明确失败，不等待流式终态。</summary>
    [Fact]
    public async Task SseIsRefusedBeforeRead()
    {
        using var stream = new CompatibleRerankReadStream(Encoding.UTF8.GetBytes("data: never ending\n\n")); using var factory = CompatibleRerankFixture.Factory(); factory.OnSend = (_, _) =>
        { var content = new StreamContent(stream); content.Headers.ContentType = new("text/event-stream"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }); };
        await Assert.ThrowsAsync<InvalidDataException>(() => new OpenAiCompatibleRerankAdapter(factory, CompatibleRerankFixture.Options()).RerankAsync(CompatibleRerankFixture.Request(), TestContext.Current.CancellationToken)); Assert.False(stream.Eof); Assert.True(stream.Disposed);
    }
}
