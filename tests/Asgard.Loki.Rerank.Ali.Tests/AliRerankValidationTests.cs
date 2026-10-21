using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Rerank.Ali.Tests;

/// <summary>验证原生嵌套参数、原文档关联及精确数字，不把坏结果当成合法排名。</summary>
public sealed class AliRerankValidationTests
{
    /// <summary>缺失、错误类型和混淆外壳在创建 HTTP 客户端前失败，不自动搬移参数或猜测模态。</summary>
    [Theory] [InlineData("missing-input")] [InlineData("null-input")] [InlineData("flat-input")] [InlineData("missing-query")]
    [InlineData("empty-query")] [InlineData("number-query")] [InlineData("video-query")] [InlineData("mixed-query")]
    [InlineData("missing-documents")] [InlineData("empty-documents")] [InlineData("null-document")] [InlineData("empty-document")]
    [InlineData("unknown-document")] [InlineData("number-document")] [InlineData("mixed-document")] [InlineData("null-media")]
    [InlineData("model")] [InlineData("flat-top")] [InlineData("null-parameters")] [InlineData("array-parameters")]
    [InlineData("null-top")] [InlineData("zero-top")] [InlineData("fraction-top")] [InlineData("near-integer-top")]
    [InlineData("negative-top")] [InlineData("string-top")] [InlineData("null-flag")] [InlineData("wrong-flag")]
    [InlineData("null-instruct")] [InlineData("negative-fps")] [InlineData("too-large-fps")] [InlineData("null-fps")]
    public async Task InvalidParameters_DoNotCreateClient(string kind)
    {
        var parameters = AliRerankFixtures.Read("top", "parameters"); var input = parameters["input"]!.AsObject(); var settings = parameters["parameters"]!.AsObject();
        switch (kind)
        {
            case "missing-input": parameters.Remove("input"); break;
            case "null-input": parameters["input"] = null; break;
            case "flat-input": parameters["query"] = "x"; break;
            case "missing-query": input.Remove("query"); break;
            case "empty-query": input["query"] = ""; break;
            case "number-query": input["query"] = 1; break;
            case "video-query": input["query"] = new JsonObject { ["video"] = "https://media.invalid/a.mp4" }; break;
            case "mixed-query": input["query"] = new JsonObject { ["text"] = "x", ["image"] = "url" }; break;
            case "missing-documents": input.Remove("documents"); break;
            case "empty-documents": input["documents"] = new JsonArray(); break;
            case "null-document": input["documents"]![0] = null; break;
            case "empty-document": input["documents"]![0] = ""; break;
            case "unknown-document": input["documents"]![0] = new JsonObject { ["unknown"] = "value" }; break;
            case "number-document": input["documents"]![0] = 1; break;
            case "mixed-document": input["documents"]![0] = new JsonObject { ["text"] = "x", ["video"] = "url" }; break;
            case "null-media": input["documents"]![0] = new JsonObject { ["image"] = null }; break;
            case "model": parameters["model"] = "override"; break;
            case "flat-top": parameters["top_n"] = 1; break;
            case "null-parameters": parameters["parameters"] = null; break;
            case "array-parameters": parameters["parameters"] = new JsonArray(); break;
            case "null-top": settings["top_n"] = null; break;
            case "zero-top": settings["top_n"] = 0; break;
            case "fraction-top": settings["top_n"] = 1.5; break;
            case "near-integer-top": settings["top_n"] = JsonNode.Parse("1.0000000000000000000000001"); break;
            case "negative-top": settings["top_n"] = -1; break;
            case "string-top": settings["top_n"] = "2"; break;
            case "null-flag": settings["return_documents"] = null; break;
            case "wrong-flag": settings["return_documents"] = 1; break;
            case "null-instruct": settings["instruct"] = null; break;
            case "negative-fps": settings["fps"] = JsonNode.Parse("-1e-10000"); break;
            case "too-large-fps": settings["fps"] = JsonNode.Parse("1.0000000000000000000000001"); break;
            case "null-fps": settings["fps"] = null; break;
        }
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new AliRerankTestFactory(handler);
        var adapter = new AliRerankAdapter(factory, new() { Model = "native", ApiKey = "test", BaseAddress = new("https://ali.invalid/api/v1/") });
        await Assert.ThrowsAnyAsync<ArgumentException>(() => adapter.RerankAsync(AliRerankFixtures.Request(parameters), TestContext.Current.CancellationToken));
        Assert.Empty(factory.Names); Assert.Equal(0, handler.Calls);
    }

    /// <summary>缺失原生标识、用量、排名或无效唯一索引不能补造为成功；原始索引按输入数组核对。</summary>
    [Theory] [InlineData("request-id")] [InlineData("empty-id")] [InlineData("output")] [InlineData("null-output")]
    [InlineData("results")] [InlineData("empty-results")] [InlineData("short")] [InlineData("long")] [InlineData("duplicate")]
    [InlineData("outside")] [InlineData("negative-index")] [InlineData("fraction-index")] [InlineData("near-index")]
    [InlineData("null-index")] [InlineData("missing-score")] [InlineData("string-score")] [InlineData("negative-score")]
    [InlineData("above-one")] [InlineData("overflow-score")] [InlineData("missing-usage")] [InlineData("missing-total")]
    [InlineData("fraction-total")] [InlineData("negative-total")] [InlineData("negative-prompt")] [InlineData("missing-document")]
    [InlineData("null-document")] [InlineData("wrong-text")] [InlineData("wrong-modality")] [InlineData("string-document")]
    public async Task InvalidNativeResponse_IsRejected(string kind)
    {
        var raw = AliRerankFixtures.Read("top"); var results = raw["output"]!["results"]!.AsArray();
        switch (kind)
        {
            case "request-id": raw.Remove("request_id"); break;
            case "empty-id": raw["request_id"] = " "; break;
            case "output": raw.Remove("output"); break;
            case "null-output": raw["output"] = null; break;
            case "results": raw["output"]!.AsObject().Remove("results"); break;
            case "empty-results": results.Clear(); break;
            case "short": results.RemoveAt(1); break;
            case "long": results.Add(JsonNode.Parse("""{"index":1,"relevance_score":0.1,"document":{"text":"beta"}}""")); break;
            case "duplicate": results[1]!["index"] = 2; break;
            case "outside": results[1]!["index"] = 3; break;
            case "negative-index": results[1]!["index"] = -1; break;
            case "fraction-index": results[1]!["index"] = 0.5; break;
            case "near-index": results[1]!["index"] = JsonNode.Parse("0.00000000000000000001"); break;
            case "null-index": results[1]!["index"] = null; break;
            case "missing-score": results[1]!.AsObject().Remove("relevance_score"); break;
            case "string-score": results[1]!["relevance_score"] = "0.5"; break;
            case "negative-score": results[1]!["relevance_score"] = JsonNode.Parse("-1e-10000"); break;
            case "above-one": results[1]!["relevance_score"] = JsonNode.Parse("1.0000000000000000000001"); break;
            case "overflow-score": results[1]!["relevance_score"] = JsonNode.Parse("1e10000"); break;
            case "missing-usage": raw.Remove("usage"); break;
            case "missing-total": raw["usage"]!.AsObject().Remove("total_tokens"); break;
            case "fraction-total": raw["usage"]!["total_tokens"] = JsonNode.Parse("1.0000000000000000000001"); break;
            case "negative-total": raw["usage"]!["total_tokens"] = -1; break;
            case "negative-prompt": raw["usage"]!["prompt_tokens"] = -1; break;
            case "missing-document": results[1]!.AsObject().Remove("document"); break;
            case "null-document": results[1]!["document"] = null; break;
            case "wrong-text": results[1]!["document"]!["text"] = "gamma"; break;
            case "wrong-modality": results[1]!["document"] = new JsonObject { ["image"] = "alpha😀" }; break;
            case "string-document": results[1]!["document"] = "alpha😀"; break;
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(AliRerankFixtures.Json(raw)));
        await using var client = AliRerankFixtures.Build(AliRerankFixtures.Adapter(handler));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Rerank.RerankAsync(AliRerankFixtures.Request("top"), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>媒体返回的显式模态或回显内容错误都不能靠同一个 URL 或索引蒙混过关。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ReturnedMedia_MustMatchOriginalInput(bool wrongValue)
    {
        var raw = AliRerankFixtures.Read("echo");
        raw["output"]!["results"]![0]!["document"] = wrongValue ? new JsonObject { ["video"] = "https://wrong.invalid/a.mp4" }
            : new JsonObject { ["image"] = "https://media.invalid/v.mp4" };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(AliRerankFixtures.Json(raw)));
        await Assert.ThrowsAsync<InvalidDataException>(() => AliRerankFixtures.Adapter(handler).RerankAsync(AliRerankFixtures.Request("echo"), TestContext.Current.CancellationToken));
    }

    /// <summary>顶层平铺结果、数组/null 或截断数据不能冒充原生 output.results。</summary>
    [Theory] [InlineData("[]")] [InlineData("null")] [InlineData("{\"results\":[{\"index\":0,\"relevance_score\":1}]}")]
    public async Task ForeignEnvelope_IsNotSuccessfulRanking(string body)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        await Assert.ThrowsAsync<InvalidDataException>(() => AliRerankFixtures.Adapter(handler).RerankAsync(AliRerankFixtures.Request(), TestContext.Current.CancellationToken));
    }

    /// <summary>损坏、多 JSON 值或任意深度重复属性都拒绝。</summary>
    [Theory] [InlineData("{")] [InlineData("{}\n{}")]
    [InlineData("{\"request_id\":\"a\",\"request_id\":\"b\"}")] [InlineData("{\"output\":{\"x\":1,\"x\":2}}")]
    public async Task MalformedJson_IsRejected(string body)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        await Assert.ThrowsAnyAsync<JsonException>(() => AliRerankFixtures.Adapter(handler).RerankAsync(AliRerankFixtures.Request(), TestContext.Current.CancellationToken));
    }

    /// <summary>独立视图构造也校验索引、分数和文档外壳，且保留 JSON 生命周期独立性。</summary>
    [Theory] [InlineData("{}")] [InlineData("{\"index\":0,\"relevance_score\":2}")]
    [InlineData("{\"index\":0,\"relevance_score\":1,\"document\":\"text\"}")]
    public void InvalidItemView_CannotBypassValidation(string body)
    {
        using var document = JsonDocument.Parse(body); Assert.Throws<InvalidDataException>(() => new AliRerankItem(document.RootElement));
    }
}
