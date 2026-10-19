using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Embedding.Ollama.Tests;

/// <summary>验证原生输入、完整向量和 HTTP 错误，不让未知结果外壳伪装成功嵌入。</summary>
public sealed class OllamaEmbeddingValidationTests
{
    /// <summary>非法已知输入或设置在创建 HTTP 客户端前失败，不补齐或删除原生字段。</summary>
    [Theory]
    [InlineData("{\"input\":3}")] [InlineData("{\"input\":false}")] [InlineData("{\"input\":[1,2]}")]
    [InlineData("{\"input\":[[1,2]]}")] [InlineData("{\"input\":[\"x\",null]}")] [InlineData("{\"input\":{}}")]
    [InlineData("{\"input\":{\"text\":\"\"}}")] [InlineData("{\"input\":{\"text\":null}}")] [InlineData("{\"input\":{\"image\":[]}}")]
    [InlineData("{\"input\":{\"audio\":\"https://media.invalid/a\"}}")] [InlineData("{\"input\":{\"image\":\"AQ ID\"}}")]
    [InlineData("{\"input\":{\"image\":\"AQID_\"}}")] [InlineData("{\"input\":{\"image\":\"data:image/png;base64,AQID\"}}")]
    [InlineData("{\"input\":{\"text\":\"x\",\"video\":\"AQID\"}}")]
    [InlineData("{\"input\":\"x\",\"model\":\"override\"}")] [InlineData("{\"input\":\"x\",\"dimensions\":1.5}")]
    [InlineData("{\"input\":\"x\",\"dimensions\":3.0}")] [InlineData("{\"input\":\"x\",\"dimensions\":\"3\"}")]
    [InlineData("{\"input\":\"x\",\"dimensions\":9223372036854775808}")] [InlineData("{\"input\":\"x\",\"truncate\":0}")]
    [InlineData("{\"input\":\"x\",\"options\":[]}")] [InlineData("{\"input\":\"x\",\"keep_alive\":false}")]
    [InlineData("{\"input\":\"x\",\"keep_alive\":{}}")] [InlineData("{\"input\":\"x\",\"keep_alive\":1e999}")]
    public async Task InvalidParameters_DoNotCallUpstream(string json)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new OllamaEmbeddingTestFactory(handler);
        var adapter = new OllamaEmbeddingAdapter(factory, new() { Model = "embedding-test" });
        await Assert.ThrowsAnyAsync<ArgumentException>(() => adapter.EmbedAsync(OllamaEmbeddingFixtures.Request(JsonNode.Parse(json)!.AsObject()), TestContext.Current.CancellationToken));
        Assert.Empty(factory.Names); Assert.Equal(0, handler.Calls);
    }

    /// <summary>直接调用适配器也验证请求契约，不依赖能力核心预先清洗。</summary>
    [Theory] [InlineData("null-request")] [InlineData("model")] [InlineData("parameters")] [InlineData("undefined")]
    public async Task InvalidDirectRequest_IsRejected(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        var request = kind switch
        {
            "null-request" => null!, "model" => new EmbeddingRequest { Model = " ", Parameters = [] },
            "parameters" => new EmbeddingRequest { Model = "public", Parameters = null! },
            _ => new EmbeddingRequest { Model = "public", Parameters = new() { ["input"] = default } }
        };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => OllamaEmbeddingFixtures.Adapter(handler).EmbedAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>数量、顺序位置的维度和每个有限数值都必须有效，不能以默认值或补造索引修复。</summary>
    [Theory] [InlineData("missing-model")] [InlineData("null-model")] [InlineData("empty-model")] [InlineData("wrong-model-type")]
    [InlineData("missing-embeddings")] [InlineData("null-embeddings")] [InlineData("object-embeddings")] [InlineData("short")]
    [InlineData("long")] [InlineData("empty-vector")] [InlineData("null-vector")] [InlineData("object-vector")]
    [InlineData("different-dimensions")] [InlineData("number-string")] [InlineData("null-value")] [InlineData("bool-value")]
    [InlineData("overflow-value")] [InlineData("native-legacy-envelope")]
    public async Task InvalidResponse_IsNotSuccessfulEmbedding(string kind)
    {
        var raw = OllamaEmbeddingFixtures.Read("batch"); var embeddings = raw["embeddings"]!.AsArray();
        switch (kind)
        {
            case "missing-model": raw.Remove("model"); break;
            case "null-model": raw["model"] = null; break;
            case "empty-model": raw["model"] = " "; break;
            case "wrong-model-type": raw["model"] = false; break;
            case "missing-embeddings": raw.Remove("embeddings"); break;
            case "null-embeddings": raw["embeddings"] = null; break;
            case "object-embeddings": raw["embeddings"] = new JsonObject(); break;
            case "short": embeddings.RemoveAt(1); break;
            case "long": embeddings.Add(new JsonArray(0.5, 0.6)); break;
            case "empty-vector": embeddings[0] = new JsonArray(); break;
            case "null-vector": embeddings[0] = null; break;
            case "object-vector": embeddings[0] = new JsonObject { ["embedding"] = new JsonArray(0.1, 0.2) }; break;
            case "different-dimensions": embeddings[1]!.AsArray().Add(0.5); break;
            case "number-string": embeddings[1]![0] = "0.3"; break;
            case "null-value": embeddings[1]![0] = null; break;
            case "bool-value": embeddings[1]![0] = true; break;
            case "overflow-value": embeddings[1]![0] = JsonNode.Parse("1e999"); break;
            case "native-legacy-envelope": raw.Remove("embeddings"); raw["embedding"] = new JsonArray(0.1, 0.2); break;
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(OllamaEmbeddingFixtures.Json(raw)));
        await using var client = OllamaEmbeddingFixtures.Build(OllamaEmbeddingFixtures.Adapter(handler));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Embedding.EmbedAsync(OllamaEmbeddingFixtures.Request("batch"), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>未指定输出维度也不能接受同批不一致尺寸，指定尺寸则逐项严格检查。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DimensionMismatch_FailsWithOrWithoutExplicitDimension(bool specified)
    {
        var parameters = OllamaEmbeddingFixtures.Read("batch", "parameters");
        if (specified) parameters["dimensions"] = 3; else parameters.Remove("dimensions");
        var raw = OllamaEmbeddingFixtures.Read("batch"); if (!specified) raw["embeddings"]![1]!.AsArray().Add(0.4);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(OllamaEmbeddingFixtures.Json(raw)));
        await Assert.ThrowsAsync<InvalidDataException>(() => OllamaEmbeddingFixtures.Adapter(handler).EmbedAsync(OllamaEmbeddingFixtures.Request(parameters), TestContext.Current.CancellationToken));
    }

    /// <summary>Int64 计数既不经过 double 舍入也不填零，原生缺省字段可以省略。</summary>
    [Theory] [InlineData("total_duration", "-1")] [InlineData("load_duration", "null")] [InlineData("prompt_eval_count", "1.5")]
    [InlineData("total_duration", "9223372036854775808")] [InlineData("load_duration", "\"1\"")]
    [InlineData("prompt_eval_count", "1.00000000000000000000000001")]
    public async Task InvalidCounter_IsRejectedExactly(string name, string value)
    {
        var raw = OllamaEmbeddingFixtures.Read(); raw[name] = JsonNode.Parse(value);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(OllamaEmbeddingFixtures.Json(raw)));
        await Assert.ThrowsAsync<InvalidDataException>(() => OllamaEmbeddingFixtures.Adapter(handler).EmbedAsync(OllamaEmbeddingFixtures.Request(), TestContext.Current.CancellationToken));
    }

    /// <summary>HTTP 和成功状态内的业务错误均保留原始状态与正文，不自动重发。</summary>
    [Theory] [InlineData(400)] [InlineData(404)] [InlineData(429)] [InlineData(500)] [InlineData(502)] [InlineData(200)]
    public async Task UpstreamErrors_RetainStatusAndBody(int status)
    {
        const string body = "{\"error\":\"the input length exceeds the context length\",\"future\":false}";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        var error = await Assert.ThrowsAsync<EmbeddingUpstreamException>(() => OllamaEmbeddingFixtures.Adapter(handler).EmbedAsync(OllamaEmbeddingFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.Equal(body, error.ResponseBody); Assert.Equal(1, handler.Calls);
    }

    /// <summary>完整响应不能含重复属性、多个 JSON 值或截断数据。</summary>
    [Theory] [InlineData("{")] [InlineData("{}\n{}")]
    [InlineData("{\"model\":\"a\",\"model\":\"b\",\"embeddings\":[[1]]}")]
    [InlineData("{\"model\":\"a\",\"embeddings\":[[1]],\"future\":{\"x\":1,\"x\":2}}")]
    public async Task MalformedJson_IsRejected(string body)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        await Assert.ThrowsAnyAsync<JsonException>(() => OllamaEmbeddingFixtures.Adapter(handler).EmbedAsync(OllamaEmbeddingFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>独立向量视图也验证原生协议，不把其他供应商格式当作有序原生列表。</summary>
    [Theory] [InlineData("[]")] [InlineData("[null]")] [InlineData("[\"1\"]")] [InlineData("[1e999]")] [InlineData("{}")]
    public void InvalidVectorView_RejectsMalformedNativeArray(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => new OllamaEmbeddingVector(document.RootElement));
    }

    /// <summary>合法 JSON 的数组、null 或布尔值仍不是完整原生成功响应。</summary>
    [Theory] [InlineData("[]")] [InlineData("null")] [InlineData("false")]
    public async Task NonObjectResponse_IsRejected(string body)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        await Assert.ThrowsAsync<InvalidDataException>(() => OllamaEmbeddingFixtures.Adapter(handler).EmbedAsync(OllamaEmbeddingFixtures.Request(), TestContext.Current.CancellationToken));
    }

    /// <summary>核心结果的原生 JSON 由供应商视图重新验证，错误或不兼容外壳不能取得数值列表。</summary>
    [Theory] [InlineData("{\"model\":\"x\",\"data\":[{\"embedding\":[1]}]}")]
    [InlineData("{\"model\":\"x\",\"error\":\"failed\",\"embeddings\":[[1]]}")]
    public void ForeignOrErrorResultView_CannotBypassNativeValidation(string body)
    {
        using var document = JsonDocument.Parse(body); var result = new EmbeddingResult(document.RootElement, new("public", "x", "native"));
        Assert.Throws<InvalidDataException>(() => result.OllamaEmbeddings);
    }
}
