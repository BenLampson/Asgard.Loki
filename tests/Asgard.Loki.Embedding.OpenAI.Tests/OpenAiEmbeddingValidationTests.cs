using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Embedding.OpenAI.Tests;

/// <summary>验证错误输入、缺失结果、索引关联和编码维度不能被静默修复。</summary>
public sealed class OpenAiEmbeddingValidationTests
{
    /// <summary>无效原生输入不触发网络请求，包括混合批类型和错误 token ID。</summary>
    [Theory]
    [InlineData("null")] [InlineData("\"\"")] [InlineData("[]")] [InlineData("[[]]")] [InlineData("[\"\"]")]
    [InlineData("[\"a\",1]")] [InlineData("[1,-1]")] [InlineData("[1,0.5]")] [InlineData("[\"a\",null]")]
    [InlineData("{}")] [InlineData("true")] [InlineData("[\"a\",[1]]")] [InlineData("[[1.5]]")] [InlineData("[2147483648]")]
    public async Task InvalidInput_FailsBeforeHttp(string input)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("无效输入不能发送。"));
        await using var client = OpenAiEmbeddingFixtures.Build(OpenAiEmbeddingFixtures.Adapter(handler));
        var request = OpenAiEmbeddingFixtures.Request(); request.Parameters["input"] = JsonSerializer.SerializeToElement(JsonNode.Parse(input));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Embedding.EmbedAsync(request, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }

    /// <summary>缺失 input、非正整数维度、未知编码及根模型覆盖在发送前失败，直接适配器入口同样校验。</summary>
    [Theory] [InlineData("missing")] [InlineData("dimension-zero")] [InlineData("dimension-negative")] [InlineData("dimension-fraction")]
    [InlineData("dimension-null")] [InlineData("encoding")] [InlineData("encoding-null")] [InlineData("model")]
    [InlineData("undefined")] [InlineData("parameters-null")]
    public async Task InvalidParameters_DirectAdapterAlsoRejects(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var request = OpenAiEmbeddingFixtures.Request();
        switch (kind)
        {
            case "missing": request.Parameters.Remove("input"); break;
            case "dimension-zero": request.Parameters["dimensions"] = JsonSerializer.SerializeToElement(0); break;
            case "dimension-negative": request.Parameters["dimensions"] = JsonSerializer.SerializeToElement(-1); break;
            case "dimension-fraction": request.Parameters["dimensions"] = JsonSerializer.SerializeToElement(1.5); break;
            case "dimension-null": request.Parameters["dimensions"] = JsonSerializer.SerializeToElement<object?>(null); break;
            case "encoding": request.Parameters["encoding_format"] = JsonSerializer.SerializeToElement("other"); break;
            case "encoding-null": request.Parameters["encoding_format"] = JsonSerializer.SerializeToElement<object?>(null); break;
            case "model": request.Parameters["model"] = JsonSerializer.SerializeToElement("hidden"); break;
            case "undefined": request.Parameters["future"] = default; break;
            case "parameters-null": request = request with { Parameters = null! }; break;
        }
        await Assert.ThrowsAnyAsync<ArgumentException>(() => OpenAiEmbeddingFixtures.Adapter(handler).EmbedAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>不能按数组位置猜索引、补空向量、重排结果或默默把不匹配的返回编码改成请求编码。</summary>
    [Theory]
    [InlineData("missing-data")] [InlineData("null-data")] [InlineData("empty-data")] [InlineData("missing-item")]
    [InlineData("index")] [InlineData("duplicate")] [InlineData("range")] [InlineData("negative")] [InlineData("fraction")]
    [InlineData("string-index")] [InlineData("overflow")] [InlineData("missing-vector")] [InlineData("null-vector")]
    [InlineData("empty-vector")] [InlineData("string-number")] [InlineData("overflow-number")] [InlineData("base64-invalid")]
    [InlineData("base64-empty")] [InlineData("base64-width")] [InlineData("dimensions")] [InlineData("encoding")]
    [InlineData("mixed")] [InlineData("root-type")] [InlineData("item-type")]
    public async Task InvalidNativeResult_IsNotASuccess(string kind)
    {
        var raw = OpenAiEmbeddingFixtures.Read("response-2-float"); var data = raw["data"]!.AsArray(); var item = data[0]!.AsObject();
        switch (kind)
        {
            case "missing-data": raw.Remove("data"); break;
            case "null-data": raw["data"] = null; break;
            case "empty-data": data.Clear(); break;
            case "missing-item": data.RemoveAt(1); break;
            case "index": item.Remove("index"); break;
            case "duplicate": item["index"] = 0; break;
            case "range": item["index"] = 2; break;
            case "negative": item["index"] = -1; break;
            case "fraction": item["index"] = 0.5; break;
            case "string-index": item["index"] = "1"; break;
            case "overflow": item["index"] = 2147483648L; break;
            case "missing-vector": item.Remove("embedding"); break;
            case "null-vector": item["embedding"] = null; break;
            case "empty-vector": item["embedding"] = new JsonArray(); break;
            case "string-number": item["embedding"] = new JsonArray("1", "2"); break;
            case "overflow-number": item["embedding"] = JsonNode.Parse("[1e400,0]"); break;
            case "base64-invalid": item["embedding"] = "not-base64"; break;
            case "base64-empty": item["embedding"] = ""; break;
            case "base64-width": item["embedding"] = "AQID"; break;
            case "dimensions": item["embedding"] = new JsonArray(1); break;
            case "encoding": raw = OpenAiEmbeddingFixtures.Read("response-2-base64"); break;
            case "mixed": item["embedding"] = "AACAPwAAAMA="; break;
            case "root-type": raw["object"] = "other"; break;
            case "item-type": item["object"] = "other"; break;
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(OpenAiEmbeddingFixtures.Json(raw)));
        await using var client = OpenAiEmbeddingFixtures.Build(OpenAiEmbeddingFixtures.Adapter(handler));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Embedding.EmbedAsync(OpenAiEmbeddingFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>未声明维度或编码也不能掩盖同一批内部的长度/编码矛盾。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnspecifiedOptions_DoNotAllowInconsistentVectors(bool mixed)
    {
        var raw = OpenAiEmbeddingFixtures.Read("response-2-float"); raw["data"]![0]!["embedding"] = mixed ? JsonValue.Create("AACAPwAAAMA=") : new JsonArray(1);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(OpenAiEmbeddingFixtures.Json(raw)));
        var request = OpenAiEmbeddingFixtures.Request(); request.Parameters.Remove("dimensions"); request.Parameters.Remove("encoding_format");
        await Assert.ThrowsAsync<InvalidDataException>(() => OpenAiEmbeddingFixtures.Adapter(handler).EmbedAsync(request, TestContext.Current.CancellationToken));
    }

    /// <summary>原生向量视图不把 Google 结构或非对象根误认作 OpenAI 的空结果。</summary>
    [Fact]
    public void ProviderView_RequiresItsOwnNativeContract()
    {
        var result = new EmbeddingResult(JsonSerializer.SerializeToElement(new { embedding = new { values = new[] { 1, 2 } } }), new("public", "actual", "other"));
        Assert.Throws<InvalidDataException>(() => result.OpenAiVectors);
        using var document = JsonDocument.Parse("null"); Assert.Throws<InvalidDataException>(() => new OpenAiEmbeddingVector(document.RootElement));
    }
}
