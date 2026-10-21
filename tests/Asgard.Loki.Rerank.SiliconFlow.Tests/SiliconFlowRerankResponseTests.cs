using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Rerank.SiliconFlow.Tests;

/// <summary>完整数量、原始索引、媒体关联及精确计数失败不能成为正常排名。</summary>
public sealed class SiliconFlowRerankResponseTests
{
    /// <summary>无效身份、项、计数与正文形状的独立矩阵。</summary>
    public static TheoryData<string> Failures => ["id-missing", "id-null", "id-number", "id-empty", "results-missing", "results-null", "results-object", "results-empty",
        "index-missing", "index-negative", "index-duplicate", "index-foreign", "index-fraction", "index-rounded", "score-missing", "score-null", "score-string", "score-infinite",
        "document-changed", "document-modality", "document-string", "document-empty", "document-conflict", "count-short", "count-extra", "meta-null", "tokens-null", "token-negative", "token-fraction", "token-rounded"];
    /// <summary>失败源保留在原生回包中，不通过重排、去重、文本替换或默认值变成成功。</summary>
    [Theory] [MemberData(nameof(Failures))]
    public async Task InvalidResponse_IsRejected(string kind)
    {
        var raw = SiliconFlowRerankFixtures.Read(); var results = raw["results"]!.AsArray(); var first = results[0]!.AsObject();
        switch (kind)
        {
            case "id-missing": raw.Remove("id"); break; case "id-null": raw["id"] = null; break; case "id-number": raw["id"] = 1; break; case "id-empty": raw["id"] = ""; break;
            case "results-missing": raw.Remove("results"); break; case "results-null": raw["results"] = null; break; case "results-object": raw["results"] = new JsonObject(); break; case "results-empty": results.Clear(); break;
            case "index-missing": first.Remove("index"); break; case "index-negative": first["index"] = -1; break; case "index-duplicate": first["index"] = 0; break;
            case "index-foreign": first["index"] = 3; break; case "index-fraction": first["index"] = 0.5; break; case "index-rounded": first["index"] = JsonNode.Parse("2.00000000000000001"); break;
            case "score-missing": first.Remove("relevance_score"); break; case "score-null": first["relevance_score"] = null; break; case "score-string": first["relevance_score"] = "0.9"; break; case "score-infinite": first["relevance_score"] = JsonNode.Parse("1e10000"); break;
            case "document-changed": first["document"] = new JsonObject { ["text"] = "wrong index" }; break;
            case "document-modality": first["document"] = new JsonObject { ["image"] = "third" }; break;
            case "document-string": first["document"] = "third"; break; case "document-empty": first["document"] = new JsonObject(); break;
            case "document-conflict": first["document"] = new JsonObject { ["text"] = "third", ["image"] = "third" }; break;
            case "count-short": results.RemoveAt(2); break; case "count-extra": results.Add(first.DeepClone()); break;
            case "meta-null": raw["meta"] = null; break; case "tokens-null": raw["tokens"] = null; break;
            case "token-negative": raw["meta"]!["tokens"]!["input_tokens"] = -1; break; case "token-fraction": raw["meta"]!["tokens"]!["output_tokens"] = 0.1; break;
            case "token-rounded": raw["meta"]!["tokens"]!["image_tokens"] = JsonNode.Parse("1.00000000000000001"); break;
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(SiliconFlowRerankFixtures.Json(raw)));
        await Assert.ThrowsAsync<InvalidDataException>(() => SiliconFlowRerankFixtures.Adapter(handler).RerankAsync(SiliconFlowRerankFixtures.Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>显式要求文档时，省略或 null 均失败；相同 URL/文本也不能按返回位置串错索引。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RequiredDocuments_CannotDisappear(bool explicitNull)
    {
        var raw = SiliconFlowRerankFixtures.Read("mixed"); var first = raw["results"]![0]!.AsObject();
        if (explicitNull) first["document"] = null; else first.Remove("document");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(SiliconFlowRerankFixtures.Json(raw)));
        await Assert.ThrowsAsync<InvalidDataException>(() => SiliconFlowRerankFixtures.Adapter(handler).RerankAsync(SiliconFlowRerankFixtures.Request("mixed"), TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>元数据可省略或使用两份已公布的原生外壳；不补 model、usage、零计数或计费解释。</summary>
    [Theory] [InlineData("none")] [InlineData("legacy")] [InlineData("modern")] [InlineData("both")]
    public async Task NativeMetadataContainers_RemainIndependent(string kind)
    {
        var raw = SiliconFlowRerankFixtures.Read("scalar");
        if (kind is "legacy" or "both") raw["tokens"] = new JsonObject { ["input_tokens"] = JsonNode.Parse("1e10000"), ["output_tokens"] = 0, ["future"] = false };
        if (kind is "modern" or "both") raw["meta"] = new JsonObject { ["tokens"] = new JsonObject { ["image_tokens"] = JsonNode.Parse("0e-10000") }, ["billed_units"] = new JsonObject { ["future"] = null } };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(SiliconFlowRerankFixtures.Json(raw)));
        var result = await SiliconFlowRerankFixtures.Adapter(handler).RerankAsync(SiliconFlowRerankFixtures.Request("scalar"), TestContext.Current.CancellationToken);
        SiliconFlowRerankFixtures.Equal(raw, result.Data); Assert.False(result.Data.TryGetProperty("usage", out _)); Assert.False(result.Data.TryGetProperty("model", out _));
    }
    /// <summary>原生项视图也拒绝错误字段，而非依靠一次调用的结果校验掩盖构造器缺陷。</summary>
    [Theory] [InlineData("{\"index\":-1,\"relevance_score\":0}")] [InlineData("{\"index\":0}")] [InlineData("{\"index\":0,\"relevance_score\":\"0\"}")]
    public void StandaloneItem_RequiresNativeFields(string json)
    {
        using var document = JsonDocument.Parse(json); Assert.Throws<InvalidDataException>(() => new SiliconFlowRerankItem(document.RootElement));
    }
}
