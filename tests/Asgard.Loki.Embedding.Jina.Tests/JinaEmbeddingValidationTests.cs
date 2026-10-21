using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Embedding.Jina.Tests;

/// <summary>验证原生输入与单/多向量、token 关联和明确未实现的格式边界。</summary>
public sealed class JinaEmbeddingValidationTests
{
    /// <summary>错误输入与设置在创建 HTTP 客户端前失败，不把文档、分组或编码改成可接受的默认值。</summary>
    [Theory] [InlineData("missing-input")] [InlineData("null-input")] [InlineData("number-input")] [InlineData("token-array")]
    [InlineData("empty-object")] [InlineData("wrong-text")] [InlineData("oneof")] [InlineData("null-media")]
    [InlineData("standalone-group")] [InlineData("empty-group")] [InlineData("string-chunk")] [InlineData("nested-group")]
    [InlineData("zero-dimensions")] [InlineData("fraction-dimensions")] [InlineData("near-dimensions")] [InlineData("large-dimensions")]
    [InlineData("string-dimensions")] [InlineData("wrong-encoding")] [InlineData("bool-encoding")] [InlineData("multi-dimensions")]
    [InlineData("tokens-without-multi")] [InlineData("media-late-chunking")] [InlineData("model")]
    [InlineData("boolean-type")] [InlineData("task-type")] [InlineData("input-type")]
    public async Task InvalidParameters_DoNotCreateClient(string kind)
    {
        var request = JinaEmbeddingFixtures.Request(); var values = request.Parameters;
        switch (kind)
        {
            case "missing-input": values.Remove("input"); break;
            case "null-input": Set(values, "input", "null"); break;
            case "number-input": Set(values, "input", "1"); break;
            case "token-array": Set(values, "input", "[1,2]"); break;
            case "empty-object": Set(values, "input", "{}"); break;
            case "wrong-text": Set(values, "input", "{\"text\":1}"); break;
            case "oneof": Set(values, "input", "{\"text\":\"x\",\"image\":\"url\"}"); break;
            case "null-media": Set(values, "input", "{\"image\":null}"); break;
            case "standalone-group": Set(values, "input", "{\"content\":[{\"text\":\"x\"}]}"); break;
            case "empty-group": Set(values, "input", "[{\"content\":[]}]"); break;
            case "string-chunk": Set(values, "input", "[{\"content\":[\"x\"]}]"); break;
            case "nested-group": Set(values, "input", "[{\"content\":[{\"content\":[{\"text\":\"x\"}]}]}]"); break;
            case "zero-dimensions": Set(values, "dimensions", "0"); break;
            case "fraction-dimensions": Set(values, "dimensions", "1.5"); break;
            case "near-dimensions": Set(values, "dimensions", "2.000000000000000000000001"); break;
            case "large-dimensions": Set(values, "dimensions", "2147483648"); break;
            case "string-dimensions": Set(values, "dimensions", "\"2\""); break;
            case "wrong-encoding": Set(values, "embedding_type", "\"int8\""); break;
            case "bool-encoding": Set(values, "embedding_type", "false"); break;
            case "multi-dimensions": Set(values, "return_multivector", "true"); break;
            case "tokens-without-multi": Set(values, "return_tokenized_input", "true"); break;
            case "media-late-chunking": Set(values, "input", "{\"image\":\"url\"}"); Set(values, "late_chunking", "true"); break;
            case "model": Set(values, "model", "\"override\""); break;
            case "boolean-type": Set(values, "truncate", "0"); break;
            case "task-type": Set(values, "task", "{}"); break;
            case "input-type": Set(values, "input_type", "false"); break;
        }
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new JinaEmbeddingTestFactory(handler);
        var adapter = new JinaEmbeddingAdapter(factory, new() { Model = "native", ApiKey = "key" });
        await Assert.ThrowsAnyAsync<ArgumentException>(() => adapter.EmbedAsync(request, TestContext.Current.CancellationToken));
        Assert.Empty(factory.Names); Assert.Equal(0, handler.Calls);
    }

    /// <summary>已知未实现的 PDF 和 packed/多编码不被下载、展开或冒充 float；保留调用者源请求。</summary>
    [Theory] [InlineData("pdf")] [InlineData("pdf-batch")] [InlineData("pdf-group")]
    [InlineData("binary")] [InlineData("ubinary")] [InlineData("multiple")]
    public async Task UnsupportedNativeFormats_HaveExplicitBoundary(string kind)
    {
        var request = JinaEmbeddingFixtures.Request();
        if (kind.StartsWith("pdf", StringComparison.Ordinal)) Set(request.Parameters, "input", kind switch
        {
            "pdf-batch" => "[{\"pdf\":\"https://media.invalid/a.pdf\"}]", "pdf-group" => "[{\"content\":[{\"pdf\":\"https://media.invalid/a.pdf\"}]}]",
            _ => "{\"pdf\":\"https://media.invalid/a.pdf\"}"
        });
        else Set(request.Parameters, "embedding_type", kind == "multiple" ? "[\"float\",\"binary\"]" : "\"" + kind + "\"");
        var before = JsonSerializer.Serialize(request); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAsync<NotSupportedException>(() => JinaEmbeddingFixtures.Adapter(handler).EmbedAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.Equal(0, handler.Calls);
    }

    /// <summary>外壳、数量、原始索引、矩阵或 token 错误均不能通过丢弃字段修复。</summary>
    [Theory] [InlineData("model")] [InlineData("empty-model")] [InlineData("usage")] [InlineData("total")] [InlineData("negative-total")]
    [InlineData("fraction-total")] [InlineData("prompt-null")] [InlineData("image-negative")] [InlineData("data")] [InlineData("short")]
    [InlineData("long")] [InlineData("duplicate")] [InlineData("outside")] [InlineData("negative-index")] [InlineData("fraction-index")]
    [InlineData("missing-vector")] [InlineData("both-envelopes")] [InlineData("object-kind")] [InlineData("mixed-envelopes")]
    [InlineData("null-matrix")] [InlineData("empty-vector")] [InlineData("null-value")] [InlineData("number-string")]
    [InlineData("nonfinite")] [InlineData("different-width")] [InlineData("different-format")]
    [InlineData("token-count")] [InlineData("token-type")] [InlineData("missing-tokens")] [InlineData("null-tokens")]
    public async Task InvalidMultiVectorResponse_IsRejected(string kind)
    {
        var raw = JinaEmbeddingFixtures.Read("multi"); var data = raw["data"]!.AsArray();
        switch (kind)
        {
            case "model": raw.Remove("model"); break;
            case "empty-model": raw["model"] = " "; break;
            case "usage": raw.Remove("usage"); break;
            case "total": raw["usage"]!.AsObject().Remove("total_tokens"); break;
            case "negative-total": raw["usage"]!["total_tokens"] = -1; break;
            case "fraction-total": raw["usage"]!["total_tokens"] = JsonNode.Parse("1.00000000000000000001"); break;
            case "prompt-null": raw["usage"]!["prompt_tokens"] = null; break;
            case "image-negative": raw["usage"]!["image_tokens"] = -1; break;
            case "data": raw.Remove("data"); break;
            case "short": data.RemoveAt(1); break;
            case "long": data.Add(data[0]!.DeepClone()); break;
            case "duplicate": data[1]!["index"] = 1; break;
            case "outside": data[1]!["index"] = 2; break;
            case "negative-index": data[1]!["index"] = -1; break;
            case "fraction-index": data[1]!["index"] = 0.1; break;
            case "missing-vector": data[1]!.AsObject().Remove("embeddings"); break;
            case "both-envelopes": data[1]!["embedding"] = new JsonArray(0.1, 0.2); break;
            case "object-kind": data[1]!["object"] = "embedding"; break;
            case "mixed-envelopes": data[1] = JsonNode.Parse("""{"index":0,"embedding":[0.1,0.2]}"""); break;
            case "null-matrix": data[1]!["embeddings"] = null; break;
            case "empty-vector": data[1]!["embeddings"]![0] = new JsonArray(); break;
            case "null-value": data[1]!["embeddings"]![0]![0] = null; break;
            case "number-string": data[1]!["embeddings"]![0]![0] = "0.1"; break;
            case "nonfinite": data[1]!["embeddings"]![0]![0] = JsonNode.Parse("1e999"); break;
            case "different-width": data[1]!["embeddings"]![0]!.AsArray().Add(0.3); break;
            case "different-format": data[1]!["embeddings"]![0] = "AACAPwAAAMA="; break;
            case "token-count": data[1]!["tokenized_input"]!.AsArray().Add("extra"); break;
            case "token-type": data[1]!["tokenized_input"]![0] = 1; break;
            case "missing-tokens": data[1]!.AsObject().Remove("tokenized_input"); break;
            case "null-tokens": data[1]!["tokenized_input"] = null; break;
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(JinaEmbeddingFixtures.Json(raw)));
        await using var client = JinaEmbeddingFixtures.Build(JinaEmbeddingFixtures.Adapter(handler));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Embedding.EmbedAsync(JinaEmbeddingFixtures.Request("multi"), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>单/多外壳和实际编码必须满足明确设置；没有请求维度时也验证全批一致性。</summary>
    [Theory] [InlineData("dimension")] [InlineData("format")] [InlineData("explicit-single")] [InlineData("explicit-multi")]
    [InlineData("sparse")] [InlineData("unindexed-sparse")]
    public async Task OutputMustMatchSupportedNativeContract(string kind)
    {
        var request = JinaEmbeddingFixtures.Request(); var raw = JinaEmbeddingFixtures.Read();
        switch (kind)
        {
            case "dimension": Set(request.Parameters, "dimensions", "3"); break;
            case "format": Set(request.Parameters, "embedding_type", "\"base64\""); break;
            case "explicit-single": Set(request.Parameters, "return_multivector", "false"); raw["data"]![0] = JsonNode.Parse("""{"index":0,"embeddings":[[0.1,0.2]]}"""); break;
            case "explicit-multi": request.Parameters.Remove("dimensions"); Set(request.Parameters, "return_multivector", "true"); break;
            case "sparse": raw["data"]![0]!["embedding"] = new JsonObject { ["token"] = 0.5 }; break;
            case "unindexed-sparse": raw["data"]![0] = new JsonObject { ["token"] = 0.5 }; break;
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(JinaEmbeddingFixtures.Json(raw)));
        await Assert.ThrowsAsync<InvalidDataException>(() => JinaEmbeddingFixtures.Adapter(handler).EmbedAsync(request, TestContext.Current.CancellationToken));
    }

    /// <summary>空/损坏 Base64 和不完整 float32 字节组不能成为原生有效向量。</summary>
    [Theory] [InlineData("")] [InlineData("!")] [InlineData("AQID")]
    public async Task InvalidBase64_IsRejected(string value)
    {
        var raw = JinaEmbeddingFixtures.Read("nullable"); raw["data"]![0]!["embedding"] = value;
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(JinaEmbeddingFixtures.Json(raw)));
        await Assert.ThrowsAsync<InvalidDataException>(() => JinaEmbeddingFixtures.Adapter(handler).EmbedAsync(JinaEmbeddingFixtures.Request("nullable"), TestContext.Current.CancellationToken));
    }

    /// <summary>损坏、多 JSON 值和任意深度重复属性不当作成功返回。</summary>
    [Theory] [InlineData("{")] [InlineData("{}\n{}")]
    [InlineData("{\"model\":\"a\",\"model\":\"b\"}")] [InlineData("{\"usage\":{\"x\":1,\"x\":2}}")]
    public async Task MalformedJson_IsRejected(string body)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        await Assert.ThrowsAnyAsync<JsonException>(() => JinaEmbeddingFixtures.Adapter(handler).EmbedAsync(JinaEmbeddingFixtures.Request(), TestContext.Current.CancellationToken));
    }

    /// <summary>单独构造视图也不能绕过矩阵 oneof、token 关联和数值验证。</summary>
    [Theory] [InlineData("{}")] [InlineData("{\"index\":0,\"embedding\":{},\"embeddings\":[]}")]
    [InlineData("{\"index\":0,\"embeddings\":[[1,2]],\"tokenized_input\":[\"a\",\"b\"]}")]
    public void InvalidItemView_IsRejected(string body)
    {
        using var document = JsonDocument.Parse(body); Assert.Throws<InvalidDataException>(() => new JinaEmbeddingItem(document.RootElement));
    }

    /// <summary>测试设置只保留原 JSON 数字表示，不借助生产代码生成预期。</summary>
    private static void Set(Dictionary<string, JsonElement> values, string key, string json)
    { using var document = JsonDocument.Parse(json); values[key] = document.RootElement.Clone(); }
}
