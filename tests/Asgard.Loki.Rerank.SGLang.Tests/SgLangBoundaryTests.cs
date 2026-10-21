using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Rerank.SGLang.Tests;

/// <summary>原生请求、精确索引、文档回显和错误协议边界，禁止修复成虚构成功。</summary>
public sealed class SgLangBoundaryTests
{
    /// <summary>无效原生请求在工厂前失败；既不补字段也不扁平化其他能力的输入。</summary>
    [Theory]
    [InlineData("query-missing")] [InlineData("query-blank")] [InlineData("documents-missing")] [InlineData("documents-empty")] [InlineData("document-blank")]
    [InlineData("query-object")] [InlineData("top-zero")] [InlineData("top-fraction")] [InlineData("top-near-integer")] [InlineData("return-type")]
    [InlineData("instruct-type")] [InlineData("media-url-shape")] [InlineData("audio-part")] [InlineData("stream")] [InlineData("model")] [InlineData("undefined")] [InlineData("duplicate-unknown")]
    public async Task InvalidNativeRequestFailsBeforeFactory(string kind)
    {
        var request = SgLangFixture.Request(); var p = request.Parameters;
        switch (kind)
        {
            case "query-missing": p.Remove("query"); break;
            case "query-blank": p["query"] = SgLangFixture.Element("\"  \""); break;
            case "documents-missing": p.Remove("documents"); break;
            case "documents-empty": p["documents"] = SgLangFixture.Element("[]"); break;
            case "document-blank": p["documents"] = SgLangFixture.Element("[\" \"]"); break;
            case "query-object": p["query"] = SgLangFixture.Element("{\"text\":\"different contract\"}"); break;
            case "top-zero": p["top_n"] = SgLangFixture.Element("0"); break;
            case "top-fraction": p["top_n"] = SgLangFixture.Element("1.5"); break;
            case "top-near-integer": p["top_n"] = SgLangFixture.Element("2.0000000000000000000001"); break;
            case "return-type": p["return_documents"] = SgLangFixture.Element("null"); break;
            case "instruct-type": p["instruct"] = SgLangFixture.Element("1"); break;
            case "media-url-shape": p["query"] = SgLangFixture.Element("""[{"type":"image_url","image_url":"different shape"}]"""); break;
            case "audio-part": p["query"] = SgLangFixture.Element("""[{"type":"audio_url","audio_url":{"url":"https://media.example.invalid/input.wav"}}]"""); break;
            case "stream": p["stream"] = SgLangFixture.Element("true"); break;
            case "model": p["MODEL"] = SgLangFixture.Element("\"override\""); break;
            case "undefined": p["future"] = default; break;
            case "duplicate-unknown": p["future"] = SgLangFixture.Element("{\"same\":1,\"same\":2}"); break;
        }
        using var factory = SgLangFixture.Factory();
        if (kind == "duplicate-unknown") await Assert.ThrowsAnyAsync<JsonException>(() => new SgLangRerankAdapter(factory, SgLangFixture.Options()).RerankAsync(request, TestContext.Current.CancellationToken));
        else await Assert.ThrowsAnyAsync<ArgumentException>(() => new SgLangRerankAdapter(factory, SgLangFixture.Options()).RerankAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, factory.Creates);
    }
    /// <summary>返回必须完整关联本次输入，score 原生且有限，不以位置/格式转换掩盖协议错误。</summary>
    [Theory]
    [InlineData("jina")] [InlineData("empty")] [InlineData("missing-item")] [InlineData("duplicate-index")] [InlineData("foreign-index")] [InlineData("negative-index")]
    [InlineData("fraction-index")] [InlineData("near-integer-index")] [InlineData("missing-index")] [InlineData("string-score")] [InlineData("missing-score")] [InlineData("overflow-score")]
    [InlineData("null-score")] [InlineData("wrong-document")] [InlineData("missing-document")] [InlineData("array-meta")]
    public async Task InvalidNativeResponseCannotBecomeRankings(string kind)
    {
        var body = JsonNode.Parse(SgLangFixture.Part("responses")[0].GetRawText())!.AsArray();
        switch (kind)
        {
            case "missing-item": body.RemoveAt(1); break;
            case "duplicate-index": body[1]!["index"] = 2; break;
            case "foreign-index": body[0]!["index"] = 3; break;
            case "negative-index": body[0]!["index"] = -1; break;
            case "fraction-index": body[0]!["index"] = 1.5; break;
            case "near-integer-index": body[0]!["index"] = JsonNode.Parse("1.99999999999999999999999"); break;
            case "missing-index": body[0]!.AsObject().Remove("index"); break;
            case "string-score": body[0]!["score"] = "1"; break;
            case "missing-score": body[0]!.AsObject().Remove("score"); break;
            case "overflow-score": body[0]!["score"] = JsonNode.Parse("1e1000000"); break;
            case "null-score": body[0]!["score"] = null; break;
            case "wrong-document": body[0]!["document"] = "Original first"; break;
            case "missing-document": body[0]!.AsObject().Remove("document"); break;
            case "array-meta": body[0]!["meta_info"] = new JsonArray(); break;
        }
        var response = kind == "jina" ? SgLangFixture.Part("foreign_jina_envelope") : SgLangFixture.Element(kind == "empty" ? "[]" : body.ToJsonString());
        using var factory = SgLangFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(SgLangFixture.Json(response));
        await Assert.ThrowsAsync<InvalidDataException>(() => new SgLangRerankAdapter(factory, SgLangFixture.Options()).RerankAsync(SgLangFixture.Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>无法与内容块原文关联的字符串回显明确失败，不用自造模板文本补造关联。</summary>
    [Fact]
    public async Task RenderedMultimodalDocumentIsNotInventedOrAssumedOriginal()
    {
        using var factory = SgLangFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(SgLangFixture.Json(SgLangFixture.Element("""[{"index":1,"score":1,"document":"invented render"}]""")));
        await Assert.ThrowsAsync<InvalidDataException>(() => new SgLangRerankAdapter(factory, SgLangFixture.Options()).RerankAsync(SgLangFixture.Request(2), TestContext.Current.CancellationToken));
    }
    /// <summary>非 JSON、截断和重复成员在完整响应验证中失败，不能发布已读前缀。</summary>
    [Theory] [InlineData("not-json")] [InlineData("[{\"index\":0")]
    public async Task IncompleteJsonFails(string body)
    {
        using var factory = SgLangFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        await Assert.ThrowsAnyAsync<JsonException>(() => new SgLangRerankAdapter(factory, SgLangFixture.Options()).RerankAsync(SgLangFixture.Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>成功 SSE 在任何正文读取前失败，重排不会等待无终止流。</summary>
    [Fact]
    public async Task SseIsRejectedBeforeBodyRead()
    {
        using var stream = new SgLangReadStream(Encoding.UTF8.GetBytes("data: never ending\n\n")); using var factory = SgLangFixture.Factory(); factory.OnSend = (_, _) =>
        { var content = new StreamContent(stream); content.Headers.ContentType = new("text/event-stream"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }); };
        await Assert.ThrowsAsync<InvalidDataException>(() => new SgLangRerankAdapter(factory, SgLangFixture.Options()).RerankAsync(SgLangFixture.Request(), TestContext.Current.CancellationToken)); Assert.False(stream.Eof); Assert.True(stream.Disposed);
    }
}
