using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>原生输入边界与分类结果关联；未知合法字段保留而不是补造模型判断。</summary>
public sealed class ModerationBoundaryTests
{
    /// <summary>无效输入或目标模型、流式控制在 HTTP 工厂前失败，不过滤/合并成其他输入。</summary>
    [Theory]
    [InlineData("missing")] [InlineData("null")] [InlineData("bool")] [InlineData("empty-array")] [InlineData("nested-array")]
    [InlineData("mixed-text-object")] [InlineData("mixed-object-text")] [InlineData("text-missing")] [InlineData("image-shape")]
    [InlineData("image-url-null")] [InlineData("unknown-type")] [InlineData("missing-type")] [InlineData("model-mismatch")] [InlineData("model-alias")] [InlineData("stream")]
    public async Task InvalidNativeInputsCannotCreateHttpResources(string kind)
    {
        var request = ModerationFixture.Payload();
        switch (kind)
        {
            case "missing": request.Remove("input"); break;
            case "null": request["input"] = null; break;
            case "bool": request["input"] = false; break;
            case "empty-array": request["input"] = new JsonArray(); break;
            case "nested-array": request["input"] = JsonNode.Parse("[[\"text\"]]"); break;
            case "mixed-text-object": request["input"] = JsonNode.Parse("""["text",{"type":"text","text":"other"}]"""); break;
            case "mixed-object-text": request["input"] = JsonNode.Parse("""[{"type":"text","text":"other"},"text"]"""); break;
            case "text-missing": request["input"] = JsonNode.Parse("""[{"type":"text"}]"""); break;
            case "image-shape": request["input"] = JsonNode.Parse("""[{"type":"image_url","image_url":"other shape"}]"""); break;
            case "image-url-null": request["input"] = JsonNode.Parse("""[{"type":"image_url","image_url":{"url":null}}]"""); break;
            case "unknown-type": request["input"] = JsonNode.Parse("""[{"type":"video_url","video_url":{"url":"https://media.example.invalid/input.mp4"}}]"""); break;
            case "missing-type": request["input"] = JsonNode.Parse("""[{"text":"no type"}]"""); break;
            case "model-mismatch": request["model"] = "different-model"; break;
            case "model-alias": request["MODEL"] = "different-model"; break;
            case "stream": request["stream"] = false; break;
        }
        using var factory = ModerationFixture.Factory(); var provider = ModerationFixture.Provider(factory);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => ((IModerationsAdapter)provider).CreateAsync(request, ModerationFixture.Context(provider), TestContext.Current.CancellationToken)); Assert.Equal(0, factory.Creates);
    }
    /// <summary>不能用缺省值、合并结果或字符串转数字掩盖缺失结果/标签字段。</summary>
    [Theory]
    [InlineData("id-missing")] [InlineData("id-empty")] [InlineData("id-number")] [InlineData("model-missing")] [InlineData("model-empty")] [InlineData("model-number")]
    [InlineData("results-missing")] [InlineData("results-null")] [InlineData("results-empty")] [InlineData("results-extra")]
    [InlineData("flagged-missing")] [InlineData("flagged-string")] [InlineData("categories-missing")] [InlineData("categories-array")]
    [InlineData("scores-missing")] [InlineData("scores-array")] [InlineData("category-type")] [InlineData("category-null")]
    [InlineData("score-null")] [InlineData("score-string")] [InlineData("score-overflow")] [InlineData("applied-array")] [InlineData("applied-value")] [InlineData("applied-item")]
    public async Task IncompleteOrMalformedResultsCannotBeSuccessful(string kind)
    {
        var body = JsonNode.Parse(ModerationFixture.Part("responses")[0].GetRawText())!.AsObject(); var item = body["results"]![0]!.AsObject();
        switch (kind)
        {
            case "id-missing": body.Remove("id"); break;
            case "id-empty": body["id"] = ""; break;
            case "id-number": body["id"] = 1; break;
            case "model-missing": body.Remove("model"); break;
            case "model-empty": body["model"] = ""; break;
            case "model-number": body["model"] = 1; break;
            case "results-missing": body.Remove("results"); break;
            case "results-null": body["results"] = null; break;
            case "results-empty": body["results"]!.AsArray().Clear(); break;
            case "results-extra": body["results"]!.AsArray().Add(item.DeepClone()); break;
            case "flagged-missing": item.Remove("flagged"); break;
            case "flagged-string": item["flagged"] = "false"; break;
            case "categories-missing": item.Remove("categories"); break;
            case "categories-array": item["categories"] = new JsonArray(); break;
            case "scores-missing": item.Remove("category_scores"); break;
            case "scores-array": item["category_scores"] = new JsonArray(); break;
            case "category-type": item["categories"]!["harassment"] = "false"; break;
            case "category-null": item["categories"]!["harassment"] = null; break;
            case "score-null": item["category_scores"]!["harassment"] = null; break;
            case "score-string": item["category_scores"]!["harassment"] = "0"; break;
            case "score-overflow": item["category_scores"]!["harassment"] = JsonNode.Parse("1e1000000"); break;
            case "applied-array": item["category_applied_input_types"] = new JsonArray(); break;
            case "applied-value": item["category_applied_input_types"]!["harassment"] = "text"; break;
            case "applied-item": item["category_applied_input_types"]!["harassment"] = new JsonArray(1); break;
        }
        using var factory = ModerationFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(ModerationFixture.Json(ModerationFixture.Element(body.ToJsonString())));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModerationFixture.Invoke(ModerationFixture.Provider(factory), 0, TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>文本批次和多模态内容组不是同一批次边界，不能自行复制/丢弃返回结果。</summary>
    [Theory] [InlineData(1, 0)] [InlineData(2, 1)]
    public async Task ResultCountMustMatchNativeInputBoundary(int requestIndex, int replyIndex)
    {
        using var factory = ModerationFixture.Factory(replyIndex);
        await Assert.ThrowsAsync<InvalidDataException>(() => ModerationFixture.Invoke(ModerationFixture.Provider(factory), requestIndex, TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>可选输入类型元信息的 null 和未来字符串值保留，不填默认列表或按模型名改写。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task OptionalAndFutureInputTypeMetadataRemainsRaw(bool nullMap)
    {
        var raw = JsonNode.Parse(ModerationFixture.Part("responses")[0].GetRawText())!.AsObject();
        if (nullMap) raw["results"]![0]!["category_applied_input_types"] = null;
        else raw["results"]![0]!["category_applied_input_types"]!["harassment"] = new JsonArray("future-modality");
        using var factory = ModerationFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(ModerationFixture.Json(ModerationFixture.Element(raw.ToJsonString())));
        var result = await ModerationFixture.Invoke(ModerationFixture.Provider(factory), 0, TestContext.Current.CancellationToken); Assert.True(JsonElement.DeepEquals(ModerationFixture.Element(raw.ToJsonString()), result.Data));
    }
}
