using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Rerank.Cohere.Tests;

/// <summary>验证无损原生输入边界、返回数量、索引身份和分数，不能用缺省值掩盖协议错误。</summary>
public sealed class CohereRerankValidationTests
{
    /// <summary>非法原生字段在创建 HTTP 客户端前失败，不自动修复或跨版本改写。</summary>
    [Theory]
    [InlineData("query-missing")] [InlineData("query-null")] [InlineData("query-number")]
    [InlineData("documents-missing")] [InlineData("documents-null")] [InlineData("documents-object")]
    [InlineData("documents-empty")] [InlineData("document-number")] [InlineData("document-null")]
    [InlineData("v2-object")] [InlineData("v1-no-text")] [InlineData("v1-text-null")]
    [InlineData("top-zero")] [InlineData("top-negative")] [InlineData("top-null")] [InlineData("top-string")]
    [InlineData("top-fraction")] [InlineData("top-rounded")] [InlineData("top-overflow")]
    [InlineData("return-null")] [InlineData("return-string")] [InlineData("fields-null")] [InlineData("fields-number")]
    [InlineData("chunks-zero")] [InlineData("tokens-zero")] [InlineData("priority-negative")] [InlineData("priority-overflow")]
    [InlineData("priority-null")] [InlineData("priority-rounded")] [InlineData("reserved-model")]
    [InlineData("undefined")] [InlineData("public-model")] [InlineData("parameters-null")]
    public async Task InvalidRequest_FailsBeforeHttp(string kind)
    {
        var request = CohereRerankFixtures.Request(); var parameters = request.Parameters;
        void Set(string key, string json) => parameters[key] = JsonSerializer.Deserialize<JsonElement>(json);
        switch (kind)
        {
            case "query-missing": parameters.Remove("query"); break;
            case "query-null": Set("query", "null"); break;
            case "query-number": Set("query", "1"); break;
            case "documents-missing": parameters.Remove("documents"); break;
            case "documents-null": Set("documents", "null"); break;
            case "documents-object": Set("documents", "{}"); break;
            case "documents-empty": Set("documents", "[]"); break;
            case "document-number": Set("documents", "[1]"); break;
            case "document-null": Set("documents", "[null]"); break;
            case "v2-object": Set("documents", "[{\"text\":\"A\"}]"); break;
            case "v1-no-text": Set("documents", "[{\"title\":\"A\"}]"); break;
            case "v1-text-null": Set("documents", "[{\"text\":null}]"); break;
            case "top-zero": Set("top_n", "0"); break;
            case "top-negative": Set("top_n", "-1"); break;
            case "top-null": Set("top_n", "null"); break;
            case "top-string": Set("top_n", "\"1\""); break;
            case "top-fraction": Set("top_n", "1.5"); break;
            case "top-rounded": Set("top_n", "1.00000000000000000000001"); break;
            case "top-overflow": Set("top_n", "2147483648"); break;
            case "return-null": Set("return_documents", "null"); break;
            case "return-string": Set("return_documents", "\"false\""); break;
            case "fields-null": Set("rank_fields", "null"); break;
            case "fields-number": Set("rank_fields", "[\"text\",1]"); break;
            case "chunks-zero": Set("max_chunks_per_doc", "0"); break;
            case "tokens-zero": Set("max_tokens_per_doc", "0"); break;
            case "priority-negative": Set("priority", "-1"); break;
            case "priority-overflow": Set("priority", "1000"); break;
            case "priority-null": Set("priority", "null"); break;
            case "priority-rounded": Set("priority", "999.0000000000000000000001"); break;
            case "reserved-model": Set("model", "\"foreign\""); break;
            case "undefined": parameters["future"] = default; break;
            case "public-model": request = request with { Model = " " }; break;
            case "parameters-null": request = request with { Parameters = null! }; break;
        }
        var v1 = kind.StartsWith("v1-", StringComparison.Ordinal) || kind.StartsWith("return-", StringComparison.Ordinal)
            || kind.StartsWith("fields-", StringComparison.Ordinal) || kind == "chunks-zero";
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new BorrowedRerankFactory(handler);
        var adapter = new CohereRerankAdapter(factory, new() { ApiVersion = v1 ? CohereRerankApiVersion.V1 : CohereRerankApiVersion.V2, Model = "actual", ApiKey = "key" });
        await Assert.ThrowsAnyAsync<ArgumentException>(() => adapter.RerankAsync(request, TestContext.Current.CancellationToken));
        Assert.Empty(factory.Names); Assert.Equal(0, handler.Calls);
    }

    /// <summary>完整原生返回必须覆盖所请求的数量，且每个索引来自本次输入；零分不能掩盖缺失分数。</summary>
    [Theory]
    [InlineData("root-null")] [InlineData("root-array")] [InlineData("missing-results")] [InlineData("results-null")]
    [InlineData("results-object")] [InlineData("empty")] [InlineData("partial")] [InlineData("extra")]
    [InlineData("duplicate")] [InlineData("foreign-index")] [InlineData("negative-index")] [InlineData("missing-index")]
    [InlineData("null-index")] [InlineData("string-index")] [InlineData("fraction-index")] [InlineData("rounded-index")]
    [InlineData("missing-score")] [InlineData("null-score")] [InlineData("string-score")] [InlineData("negative-score")]
    [InlineData("large-score")] [InlineData("infinite-score")] [InlineData("rounded-score")] [InlineData("underflow-negative")]
    [InlineData("null-item")]
    public async Task MalformedRanking_IsNotSuccessfulResult(string kind)
    {
        JsonNode? raw = CohereRerankFixtures.Read("v2-basic"); var results = raw!["results"]!.AsArray(); var item = results[0]!.AsObject();
        switch (kind)
        {
            case "root-null": raw = null; break;
            case "root-array": raw = new JsonArray(); break;
            case "missing-results": raw.AsObject().Remove("results"); break;
            case "results-null": raw["results"] = null; break;
            case "results-object": raw["results"] = new JsonObject(); break;
            case "empty": results.Clear(); break;
            case "partial": results.RemoveAt(1); break;
            case "extra": results.Add(item.DeepClone()); break;
            case "duplicate": item["index"] = 0; break;
            case "foreign-index": item["index"] = 2; break;
            case "negative-index": item["index"] = -1; break;
            case "missing-index": item.Remove("index"); break;
            case "null-index": item["index"] = null; break;
            case "string-index": item["index"] = "1"; break;
            case "fraction-index": item["index"] = 1.5; break;
            case "rounded-index": item["index"] = JsonNode.Parse("1.000000000000000000000001"); break;
            case "missing-score": item.Remove("relevance_score"); break;
            case "null-score": item["relevance_score"] = null; break;
            case "string-score": item["relevance_score"] = "0.75"; break;
            case "negative-score": item["relevance_score"] = -0.25; break;
            case "large-score": item["relevance_score"] = 1.25; break;
            case "infinite-score": item["relevance_score"] = JsonNode.Parse("1e999"); break;
            case "rounded-score": item["relevance_score"] = JsonNode.Parse("1.00000000000000000000001"); break;
            case "underflow-negative": item["relevance_score"] = JsonNode.Parse("-1e-999"); break;
            case "null-item": results[0] = null; break;
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw?.ToJsonString() ?? "null") }));
        await using var client = CohereRerankFixtures.Build(CohereRerankFixtures.Adapter(handler));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Rerank.RerankAsync(CohereRerankFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>返回文档不能缺失、换成另一条原文或经过 Unicode 规范化后冒充原始索引对应内容。</summary>
    [Theory] [InlineData("missing")] [InlineData("null")] [InlineData("text-missing")] [InlineData("text-null")]
    [InlineData("string")] [InlineData("wrong-index")] [InlineData("unicode-change")]
    public async Task V1ReturnedDocument_MustMatchOriginalIndex(string kind)
    {
        var request = CohereRerankFixtures.Request("v1-text"); var raw = CohereRerankFixtures.Read("v1-text"); var item = raw["results"]![0]!.AsObject();
        switch (kind)
        {
            case "missing": item.Remove("document"); break;
            case "null": item["document"] = null; break;
            case "text-missing": item["document"] = new JsonObject(); break;
            case "text-null": item["document"]!["text"] = null; break;
            case "string": item["document"] = "B"; break;
            case "wrong-index": item["index"] = 0; break;
            case "unicode-change": request.Parameters["documents"] = JsonSerializer.SerializeToElement(new[] { "A", "e\u0301" }); item["document"]!["text"] = "é"; break;
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(CohereRerankFixtures.Json(raw)));
        await using var client = CohereRerankFixtures.Build(CohereRerankFixtures.Adapter(handler, CohereRerankApiVersion.V1));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Rerank.RerankAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>正文相同仍必须保留原索引对应的其他字段，不能接受另一份同正文文档或字段丢失。</summary>
    [Theory] [InlineData("shared-text-wrong-title")] [InlineData("missing-field")] [InlineData("changed-unknown")]
    public async Task V1ObjectDocument_MustRetainOriginalFields(string kind)
    {
        var request = CohereRerankFixtures.Request("v1-object"); var response = CohereRerankFixtures.Read("v1-object");
        var returned = response["results"]![1]!["document"]!.AsObject();
        if (kind == "shared-text-wrong-title")
        {
            var documents = JsonNode.Parse(request.Parameters["documents"].GetRawText())!.AsArray(); documents[1]!["text"] = "A😀";
            request.Parameters["documents"] = JsonSerializer.SerializeToElement(documents); returned["title"] = "second";
        }
        else if (kind == "missing-field") returned.Remove("future");
        else returned["future"]!["null"] = false;
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(CohereRerankFixtures.Json(response)));
        await using var client = CohereRerankFixtures.Build(CohereRerankFixtures.Adapter(handler, CohereRerankApiVersion.V1));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Rerank.RerankAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>便捷视图不能把 top-n 子集索引限定为返回数组长度，且保留缺失与显式 null 的区别。</summary>
    [Fact]
    public void NativeView_SnapshotsRawItemWithoutInventingRequestContext()
    {
        using var document = JsonDocument.Parse("{\"index\":7,\"relevance_score\":-0.0,\"document\":null,\"future\":false}");
        var item = new CohereRerankItem(document.RootElement); document.Dispose();
        Assert.Equal(7, item.Index); Assert.Equal(0, item.RelevanceScore); Assert.Equal(JsonValueKind.Null, item.Document!.Value.ValueKind);
        Assert.False(item.Data.GetProperty("future").GetBoolean());
        var result = new RerankResult(JsonSerializer.Deserialize<JsonElement>("{\"results\":[{\"index\":7,\"relevance_score\":0}]}"), new("public", "native", "id"));
        Assert.Equal(7, Assert.Single(result.CohereRankings).Index); Assert.Null(result.CohereRankings[0].Document);
    }
}
