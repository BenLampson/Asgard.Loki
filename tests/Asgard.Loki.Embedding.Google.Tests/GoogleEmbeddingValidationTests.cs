using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Embedding.Google.Tests;

/// <summary>验证操作、批内模型、精确维度与返回关联不能被静默改写。</summary>
public sealed class GoogleEmbeddingValidationTests
{
    /// <summary>缺失 Content、批数组、冲突操作或外部模型均在任何 HTTP 前拒绝。</summary>
    [Theory] [InlineData("content")] [InlineData("content-null")] [InlineData("parts")] [InlineData("parts-empty")]
    [InlineData("part-null")] [InlineData("part-empty")] [InlineData("single-batch")] [InlineData("batch-content")]
    [InlineData("batch-missing")] [InlineData("batch-null")] [InlineData("batch-empty")] [InlineData("batch-item")]
    [InlineData("model-other")] [InlineData("model-null")] [InlineData("model-bare")] [InlineData("root-model")]
    public async Task InvalidNativeRequest_FailsBeforeHttp(string kind)
    {
        var batch = kind.StartsWith("batch", StringComparison.Ordinal) || kind.StartsWith("model", StringComparison.Ordinal);
        var name = batch ? "batch-config" : "single-config"; var raw = GoogleEmbeddingFixtures.Read(name);
        switch (kind)
        {
            case "content": raw.Remove("content"); break;
            case "content-null": raw["content"] = null; break;
            case "parts": raw["content"]!.AsObject().Remove("parts"); break;
            case "parts-empty": raw["content"]!["parts"] = new JsonArray(); break;
            case "part-null": raw["content"]!["parts"]![0] = null; break;
            case "part-empty": raw["content"]!["parts"]![0] = new JsonObject(); break;
            case "single-batch": raw["requests"] = new JsonArray(); break;
            case "batch-content": raw["content"] = new JsonObject(); break;
            case "batch-missing": raw.Remove("requests"); break;
            case "batch-null": raw["requests"] = null; break;
            case "batch-empty": raw["requests"] = new JsonArray(); break;
            case "batch-item": raw["requests"]![0] = null; break;
            case "model-other": raw["requests"]![1]!["model"] = "models/other"; break;
            case "model-null": raw["requests"]![1]!["model"] = null; break;
            case "model-bare": raw["requests"]![1]!["model"] = "embedding-test"; break;
        }
        var request = JsonSerializer.Deserialize<EmbeddingRequest>(raw.ToJsonString())!;
        if (kind == "root-model") request.Parameters["model"] = JsonSerializer.SerializeToElement("hidden");
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("无效请求不能发送。"));
        await using var client = GoogleEmbeddingFixtures.Build(GoogleEmbeddingFixtures.Adapter(handler, GoogleEmbeddingFixtures.Operation(name)));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.Embedding.EmbedAsync(request, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }

    /// <summary>分数、越界或舍入后恰为整数的源值不能成为维度 2；新旧外壳及别名冲突不能猜优先级。</summary>
    [Theory] [InlineData("0")] [InlineData("-1")] [InlineData("1.5")] [InlineData("2147483648")] [InlineData("1e400")]
    [InlineData("2.0000000000000000000000000000001")] [InlineData("\"2.0000000000000000000000000000001\"")]
    [InlineData("true")] [InlineData("\"bad\"")] [InlineData("\" 2 \"")]
    public async Task InvalidDimension_IsNeverRounded(string value)
    {
        var raw = GoogleEmbeddingFixtures.Read("single-legacy"); raw["outputDimensionality"] = JsonNode.Parse(value);
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAsync<ArgumentException>(() => GoogleEmbeddingFixtures.Adapter(handler)
            .EmbedAsync(JsonSerializer.Deserialize<EmbeddingRequest>(raw.ToJsonString())!, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }

    /// <summary>矛盾或重复的配置定位必须明确失败，不合并或覆盖原始字段。</summary>
    [Theory] [InlineData("conflict")] [InlineData("alias")] [InlineData("config-alias")] [InlineData("config-type")]
    public async Task AmbiguousSettings_AreRejected(string kind)
    {
        var raw = GoogleEmbeddingFixtures.Read("single-config");
        if (kind == "conflict") raw["outputDimensionality"] = 1;
        else if (kind == "alias") raw["embedContentConfig"]!["output_dimensionality"] = 2;
        else if (kind == "config-alias") raw["embed_content_config"] = null;
        else raw["embedContentConfig"] = false;
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAsync<ArgumentException>(() => GoogleEmbeddingFixtures.Adapter(handler)
            .EmbedAsync(JsonSerializer.Deserialize<EmbeddingRequest>(raw.ToJsonString())!, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }

    /// <summary>两个原生操作独立验证外壳、数值和对应维度，错误结果不能转成空列表或补零。</summary>
    [Theory] [InlineData("missing", false)] [InlineData("missing", true)] [InlineData("null", false)] [InlineData("null", true)]
    [InlineData("no-values", false)] [InlineData("no-values", true)] [InlineData("empty-values", false)] [InlineData("empty-values", true)]
    [InlineData("nan", false)] [InlineData("nan", true)] [InlineData("infinity", false)] [InlineData("infinity", true)]
    [InlineData("overflow", false)] [InlineData("overflow", true)] [InlineData("null-value", false)] [InlineData("null-value", true)]
    [InlineData("wrong-value", false)] [InlineData("wrong-value", true)] [InlineData("dimension", false)] [InlineData("dimension", true)]
    [InlineData("other-envelope", false)] [InlineData("other-envelope", true)] [InlineData("both", false)] [InlineData("both", true)]
    public async Task InvalidResponse_IsNotSuccessful(string kind, bool batch)
    {
        var name = batch ? "batch-config" : "single-config"; var raw = GoogleEmbeddingFixtures.Read(name + "-response");
        var key = batch ? "embeddings" : "embedding"; var vector = batch ? raw[key]![0]! : raw[key]!;
        switch (kind)
        {
            case "missing": raw.Remove(key); break;
            case "null": raw[key] = null; break;
            case "no-values": vector.AsObject().Remove("values"); break;
            case "empty-values": vector["values"] = new JsonArray(); break;
            case "nan": vector["values"] = new JsonArray("NaN", 0); break;
            case "infinity": vector["values"] = new JsonArray("Infinity", 0); break;
            case "overflow": vector["values"] = JsonNode.Parse("[1e400,0]"); break;
            case "null-value": vector["values"] = JsonNode.Parse("[null,0]"); break;
            case "wrong-value": vector["values"] = new JsonArray("not-numeric", 0); break;
            case "dimension": vector["values"] = new JsonArray(1); break;
            case "other-envelope": raw.Remove(key); raw[batch ? "embedding" : "embeddings"] = batch ? new JsonObject() : new JsonArray(); break;
            case "both": raw[batch ? "embedding" : "embeddings"] = null; break;
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(GoogleEmbeddingFixtures.Json(raw)));
        await using var client = GoogleEmbeddingFixtures.Build(GoogleEmbeddingFixtures.Adapter(handler, GoogleEmbeddingFixtures.Operation(name)));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Embedding.EmbedAsync(GoogleEmbeddingFixtures.Request(name), TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }

    /// <summary>数量不符或交换具有不同声明维度的结果均破坏原生位置对应，不能借重排来修复。</summary>
    [Theory] [InlineData("missing")] [InlineData("extra")] [InlineData("empty")] [InlineData("swapped")]
    public async Task BatchAssociation_IsCheckedWithoutInventingIds(string kind)
    {
        var raw = GoogleEmbeddingFixtures.Read("batch-config-response"); var values = raw["embeddings"]!.AsArray();
        if (kind == "missing") values.RemoveAt(1);
        else if (kind == "extra") values.Add(values[1]!.DeepClone());
        else if (kind == "empty") values.Clear();
        else { var one = values[0]!.DeepClone(); var two = values[1]!.DeepClone(); values[0] = two; values[1] = one; }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(GoogleEmbeddingFixtures.Json(raw)));
        await Assert.ThrowsAsync<InvalidDataException>(() => GoogleEmbeddingFixtures.Adapter(handler, GoogleEmbeddingOperation.BatchEmbedContents)
            .EmbedAsync(GoogleEmbeddingFixtures.Request("batch-config"), TestContext.Current.CancellationToken));
    }

    /// <summary>原生数值视图不把 OpenAI data 或 shape-only 内部张量格式误认为公开 Google 数值向量。</summary>
    [Theory] [InlineData("""{"data":[{"index":0,"embedding":[1]}]}""")] [InlineData("""{"embedding":{"shape":[1,2]}}""")]
    public void NativeView_RejectsOtherContracts(string json)
    {
        using var source = JsonDocument.Parse(json); var result = new EmbeddingResult(source.RootElement, new("public", "actual", "other"));
        Assert.Throws<InvalidDataException>(() => result.GoogleEmbeddings);
    }
}
