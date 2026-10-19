using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Rerank.Jina.Tests;

/// <summary>校验原生可空设置、文档模态、排名身份和必需用量，不复用其他供应商的业务规则。</summary>
public sealed class JinaRerankValidationTests
{
    /// <summary>错误的原生结构在 HTTP 工厂访问前拒绝，不修复字段或改变实际模型。</summary>
    [Theory] [InlineData("query-missing")] [InlineData("query-null")] [InlineData("query-number")]
    [InlineData("query-text-object")] [InlineData("query-ambiguous")] [InlineData("query-image-null")]
    [InlineData("documents-missing")] [InlineData("documents-null")] [InlineData("documents-object")]
    [InlineData("documents-empty")] [InlineData("document-null")] [InlineData("document-number")]
    [InlineData("document-unknown")] [InlineData("document-ambiguous")] [InlineData("document-text-number")]
    [InlineData("top-zero")] [InlineData("top-negative")] [InlineData("top-fraction")] [InlineData("top-rounded")]
    [InlineData("top-underflow")] [InlineData("top-string")]
    [InlineData("return-string")] [InlineData("embedding-number")]
    [InlineData("length-zero")] [InlineData("length-large")] [InlineData("length-string")] [InlineData("length-rounded")]
    [InlineData("reserved-model")] [InlineData("undefined")] [InlineData("public-model")] [InlineData("parameters-null")]
    public async Task InvalidRequest_FailsBeforeHttp(string kind)
    {
        var request = JinaRerankFixtures.Request(); var parameters = request.Parameters;
        void Set(string field, string json) => parameters[field] = JsonSerializer.Deserialize<JsonElement>(json);
        switch (kind)
        {
            case "query-missing": parameters.Remove("query"); break;
            case "query-null": Set("query", "null"); break;
            case "query-number": Set("query", "1"); break;
            case "query-text-object": Set("query", "{\"text\":\"query\"}"); break;
            case "query-ambiguous": Set("query", "{\"text\":\"query\",\"image\":\"opaque\"}"); break;
            case "query-image-null": Set("query", "{\"image\":null}"); break;
            case "documents-missing": parameters.Remove("documents"); break;
            case "documents-null": Set("documents", "null"); break;
            case "documents-object": Set("documents", "{}"); break;
            case "documents-empty": Set("documents", "[]"); break;
            case "document-null": Set("documents", "[null]"); break;
            case "document-number": Set("documents", "[1]"); break;
            case "document-unknown": Set("documents", "[{\"audio\":\"opaque\"}]"); break;
            case "document-ambiguous": Set("documents", "[{\"text\":\"A\",\"image\":\"opaque\"}]"); break;
            case "document-text-number": Set("documents", "[{\"text\":1}]"); break;
            case "top-zero": Set("top_n", "0"); break;
            case "top-negative": Set("top_n", "-1"); break;
            case "top-fraction": Set("top_n", "1.5"); break;
            case "top-rounded": Set("top_n", "1.000000000000000000001"); break;
            case "top-underflow": Set("top_n", "1e-999"); break;
            case "top-string": Set("top_n", "\"1\""); break;
            case "return-string": Set("return_documents", "\"false\""); break;
            case "embedding-number": Set("return_embeddings", "1"); break;
            case "length-zero": Set("max_doc_length", "0"); break;
            case "length-large": Set("max_doc_length", "8193"); break;
            case "length-string": Set("max_doc_length", "\"8192\""); break;
            case "length-rounded": Set("max_doc_length", "8192.00000000000000001"); break;
            case "reserved-model": Set("model", "\"foreign\""); break;
            case "undefined": parameters["future"] = default; break;
            case "public-model": request = request with { Model = " " }; break;
            case "parameters-null": request = request with { Parameters = null! }; break;
        }
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new BorrowedRerankFactory(handler);
        var adapter = new JinaRerankAdapter(factory, new() { Model = "actual", ApiKey = "key" });
        await Assert.ThrowsAnyAsync<ArgumentException>(() => adapter.RerankAsync(request, TestContext.Current.CancellationToken));
        Assert.Empty(factory.Names); Assert.Equal(0, handler.Calls);
    }

    /// <summary>模型、计数、关联和有限分数必须符合本次完整响应，不把错误修成空列表、默认索引或用量零。</summary>
    [Theory] [InlineData("root-null")] [InlineData("root-array")] [InlineData("missing-model")]
    [InlineData("model-null")] [InlineData("model-number")] [InlineData("missing-usage")]
    [InlineData("usage-null")] [InlineData("usage-string")] [InlineData("tokens-missing")]
    [InlineData("tokens-null")] [InlineData("tokens-negative")] [InlineData("tokens-fraction")] [InlineData("tokens-rounded")]
    [InlineData("tokens-underflow")] [InlineData("tokens-string")] [InlineData("object-null")] [InlineData("object-wrong")]
    [InlineData("results-missing")] [InlineData("results-null")] [InlineData("results-empty")]
    [InlineData("partial")] [InlineData("extra")] [InlineData("duplicate")]
    [InlineData("index-missing")] [InlineData("index-null")] [InlineData("index-string")]
    [InlineData("index-negative")] [InlineData("index-fraction")] [InlineData("index-rounded")] [InlineData("index-foreign")]
    [InlineData("score-missing")] [InlineData("score-null")] [InlineData("score-string")] [InlineData("score-infinite")]
    [InlineData("item-null")]
    public async Task MalformedResponse_IsNotSuccessfulRanking(string kind)
    {
        JsonNode? raw = JinaRerankFixtures.Read("text"); var results = raw!["results"]!.AsArray(); var item = results[0]!.AsObject();
        switch (kind)
        {
            case "root-null": raw = null; break;
            case "root-array": raw = new JsonArray(); break;
            case "missing-model": raw.AsObject().Remove("model"); break;
            case "model-null": raw["model"] = null; break;
            case "model-number": raw["model"] = 1; break;
            case "missing-usage": raw.AsObject().Remove("usage"); break;
            case "usage-null": raw["usage"] = null; break;
            case "usage-string": raw["usage"] = "0"; break;
            case "tokens-missing": raw["usage"]!.AsObject().Remove("total_tokens"); break;
            case "tokens-null": raw["usage"]!["total_tokens"] = null; break;
            case "tokens-negative": raw["usage"]!["total_tokens"] = -1; break;
            case "tokens-fraction": raw["usage"]!["total_tokens"] = 1.5; break;
            case "tokens-rounded": raw["usage"]!["total_tokens"] = JsonNode.Parse("1.00000000000000000000001"); break;
            case "tokens-underflow": raw["usage"]!["total_tokens"] = JsonNode.Parse("-1e-999"); break;
            case "tokens-string": raw["usage"]!["total_tokens"] = "0"; break;
            case "object-null": raw["object"] = null; break;
            case "object-wrong": raw["object"] = "ranking"; break;
            case "results-missing": raw.AsObject().Remove("results"); break;
            case "results-null": raw["results"] = null; break;
            case "results-empty": results.Clear(); break;
            case "partial": results.RemoveAt(1); break;
            case "extra": results.Add(item.DeepClone()); break;
            case "duplicate": item["index"] = 0; item["document"] = "A😀"; break;
            case "index-missing": item.Remove("index"); break;
            case "index-null": item["index"] = null; break;
            case "index-string": item["index"] = "1"; break;
            case "index-negative": item["index"] = -1; break;
            case "index-fraction": item["index"] = 1.5; break;
            case "index-rounded": item["index"] = JsonNode.Parse("1.00000000000000000000001"); break;
            case "index-foreign": item["index"] = 2; break;
            case "score-missing": item.Remove("relevance_score"); break;
            case "score-null": item["relevance_score"] = null; break;
            case "score-string": item["relevance_score"] = "1"; break;
            case "score-infinite": item["relevance_score"] = JsonNode.Parse("1e999"); break;
            case "item-null": results[0] = null; break;
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw?.ToJsonString() ?? "null") }));
        await using var client = JinaRerankFixtures.Build(JinaRerankFixtures.Adapter(handler));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Rerank.RerankAsync(JinaRerankFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>返回文档的索引、模态和原文须一致；不自动把媒体地址规范化或 Unicode 等价文本当作原始值。</summary>
    [Theory] [InlineData("missing-default")] [InlineData("null-default")] [InlineData("missing-explicit")]
    [InlineData("wrong-index")] [InlineData("wrong-modality")] [InlineData("ambiguous")]
    [InlineData("invalid-container")] [InlineData("unicode-change")] [InlineData("media-change")] [InlineData("media-wrong-modality")]
    public async Task ReturnedDocument_MustMatchNativeSource(string kind)
    {
        var key = kind.StartsWith("media-", StringComparison.Ordinal) ? "multimodal-image-query" : "text";
        var request = JinaRerankFixtures.Request(key); var raw = JinaRerankFixtures.Read(key); var item = raw["results"]![0]!.AsObject();
        switch (kind)
        {
            case "missing-default": item.Remove("document"); break;
            case "null-default": item["document"] = null; break;
            case "missing-explicit": request.Parameters["return_documents"] = JsonSerializer.SerializeToElement(true); item.Remove("document"); break;
            case "wrong-index": item["index"] = 0; break;
            case "wrong-modality": item["document"] = new JsonObject { ["image"] = "B" }; break;
            case "ambiguous": item["document"] = new JsonObject { ["text"] = "B", ["image"] = "B" }; break;
            case "invalid-container": item["document"] = 1; break;
            case "unicode-change": request.Parameters["documents"] = JsonSerializer.SerializeToElement(new[] { "A", "e\u0301" }); item["document"] = "é"; break;
            case "media-change": raw["results"]![1]!["document"]!["image"] = "https://media.invalid/other.png"; break;
            case "media-wrong-modality": raw["results"]![1]!["document"] = new JsonObject { ["text"] = "https://media.invalid/candidate.png" }; break;
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(JinaRerankFixtures.Json(raw)));
        await using var client = JinaRerankFixtures.Build(JinaRerankFixtures.Adapter(handler));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Rerank.RerankAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>原生向量仅按本次索引附着，显式要求时不得缺失，也不能把非法数值或编码冒充向量。</summary>
    [Theory] [InlineData("missing")] [InlineData("null")] [InlineData("empty")] [InlineData("string")]
    [InlineData("null-value")] [InlineData("string-value")] [InlineData("infinite-value")]
    public async Task RequestedEmbedding_MustBePresentAndValid(string kind)
    {
        var raw = JinaRerankFixtures.Read("listwise"); var item = raw["results"]![0]!.AsObject();
        switch (kind)
        {
            case "missing": item.Remove("embedding"); break;
            case "null": item["embedding"] = null; break;
            case "empty": item["embedding"] = new JsonArray(); break;
            case "string": item["embedding"] = "base64"; break;
            case "null-value": item["embedding"] = new JsonArray((JsonNode?)null); break;
            case "string-value": item["embedding"] = new JsonArray("1"); break;
            case "infinite-value": item["embedding"] = JsonNode.Parse("[1e999]"); break;
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(JinaRerankFixtures.Json(raw)));
        await Assert.ThrowsAsync<InvalidDataException>(() => JinaRerankFixtures.Adapter(handler).RerankAsync(JinaRerankFixtures.Request("listwise"), TestContext.Current.CancellationToken));
    }

    /// <summary>无原请求的供应商视图只验证自身，不以返回数组长度限制子集索引，也不合并缺失与显式 null。</summary>
    [Fact]
    public void NativeView_ClonesDataAndVectorWithoutInventingContext()
    {
        using var document = JsonDocument.Parse("{\"index\":7,\"relevance_score\":-2.5,\"document\":null,\"embedding\":[0,2],\"future\":false}");
        var item = new JinaRerankItem(document.RootElement); document.Dispose();
        Assert.Equal(7, item.Index); Assert.Equal(-2.5, item.RelevanceScore); Assert.Equal(JsonValueKind.Null, item.Document!.Value.ValueKind);
        var values = Assert.IsType<double[]>(item.Embedding); values[0] = 99; Assert.Equal(0, item.Embedding![0]);
        Assert.False(item.Data.GetProperty("future").GetBoolean());
        var raw = JsonSerializer.Deserialize<JsonElement>("{\"model\":\"native\",\"usage\":{\"total_tokens\":0},\"results\":[{\"index\":7,\"relevance_score\":2.5}]}");
        var result = new RerankResult(raw, new("public", "native", "id")); Assert.Equal(7, Assert.Single(result.JinaRankings).Index); Assert.Null(result.JinaRankings[0].Document);
    }
}
