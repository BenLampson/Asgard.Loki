using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Embedding.OpenAI.Tests;

/// <summary>用实际适配器证明可复用的 Jina 子集和原生差异，不把字段透传等同完整供应商实现。</summary>
public sealed class JinaCompatibilityTests
{
    /// <summary>Jina 使用原生 embedding_type/task 等参数，最终正文不添加 encoding_format 或流开关。</summary>
    [Theory] [InlineData("text")] [InlineData("settings")] [InlineData("base64")]
    public async Task NativeTextSubset_MatchesIndependentWireGolden(string name)
    {
        var raw = JinaCompatibilityFixtures.Read(name);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://jina.invalid/tenant/v1/embeddings", message.RequestUri!.AbsoluteUri); Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("jina-fixture-key", message.Headers.Authorization.Parameter);
            Assert.False(message.Headers.Contains("OpenAI-Organization")); Assert.False(message.Headers.Contains("OpenAI-Project"));
            Assert.True(JsonNode.DeepEquals(JinaCompatibilityFixtures.Read(name, "wire"), JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(raw.ToJsonString())) };
            response.Content.Headers.ContentType = new("application/json"); return response;
        });
        await using var client = OpenAiEmbeddingFixtures.Build(JinaCompatibilityFixtures.Adapter(handler)); var request = JinaCompatibilityFixtures.Request(name);
        var before = JsonSerializer.Serialize(request); var result = await client.Embedding.EmbedAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.True(JsonNode.DeepEquals(raw, JsonNode.Parse(result.Data.GetRawText())));
        Assert.Equal("public", result.Metadata.PublicModel); Assert.Equal("jina-embeddings-v3", result.Metadata.UpstreamModel); Assert.Equal("jina-compatible", result.Metadata.ProviderId);
        var expected = raw["data"]!.AsArray(); var vectors = result.OpenAiVectors; Assert.Equal(expected.Count, vectors.Count);
        for (var i = 0; i < vectors.Count; i++)
        {
            Assert.Equal(expected[i]!["index"]!.GetValue<int>(), vectors[i].Index);
            Assert.True(JsonNode.DeepEquals(expected[i], JsonNode.Parse(vectors[i].Data.GetRawText())));
        }
        if (name == "base64")
        {
            Assert.Null(vectors[0].Values); Assert.Equal("AACAPwAAAMA=", vectors[0].Base64);
            Assert.Equal(new byte[] { 0, 0, 128, 63, 0, 0, 0, 192 }, vectors[0].DecodeBytes().ToArray());
        }
        else Assert.Equal(new double[] { name == "settings" ? 0.3 : 0.1, name == "settings" ? 0.4 : -0.2 }, vectors[0].Values);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Jina 原生文档/媒体/融合输入不能经 OpenAI 分词数组规则偷偷改成普通文本。</summary>
    [Theory] [InlineData("{\"text\":\"x\"}")] [InlineData("[{\"image\":\"https://media.invalid/i.png\"}]")]
    [InlineData("[{\"audio\":\"https://media.invalid/a.wav\"}]")] [InlineData("[{\"video\":\"https://media.invalid/v.mp4\"}]")]
    [InlineData("{\"pdf\":\"https://media.invalid/doc.pdf\"}")]
    [InlineData("[{\"content\":[{\"text\":\"x\"},{\"image\":\"https://media.invalid/i.png\"}]}]")]
    [InlineData("[\"x\",{\"text\":\"y\"}]")]
    public async Task NativeObjectInput_IsAnExplicitCompatibilityBoundary(string input)
    {
        var request = JinaCompatibilityFixtures.Request(); request.Parameters["input"] = JsonSerializer.SerializeToElement(JsonNode.Parse(input));
        var before = JsonSerializer.Serialize(request);
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAnyAsync<ArgumentException>(() => JinaCompatibilityFixtures.Adapter(handler, "jina-embeddings-v5-omni-small")
            .EmbedAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.Equal(0, handler.Calls);
    }

    /// <summary>原生多向量、稀疏和 bit-packed 维度不能被当作已验证的 OpenAI dense 协议。</summary>
    [Theory] [InlineData("multi")] [InlineData("sparse-indexed")] [InlineData("sparse-ordered")] [InlineData("binary")] [InlineData("ubinary")]
    public async Task NativeOutputDifference_RemainsVisible(string kind)
    {
        var request = JinaCompatibilityFixtures.Request(); var raw = JinaCompatibilityFixtures.Read();
        var model = kind == "multi" ? "jina-embeddings-v4" : kind.StartsWith("sparse", StringComparison.Ordinal) ? "schema-sparse-fixture" : "jina-embeddings-v3";
        raw["model"] = model;
        switch (kind)
        {
            case "multi":
                request.Parameters["return_multivector"] = JsonSerializer.SerializeToElement(true);
                raw["data"]![0] = JsonNode.Parse("""{"object":"embeddings","index":0,"embeddings":[[0.1,0.2],[0.3,0.4]],"tokenized_input":["a","b"]}"""); break;
            case "sparse-indexed": raw["data"]![0]!["embedding"] = new JsonObject { ["token"] = 0.5 }; break;
            case "sparse-ordered": raw["data"]![0] = new JsonObject { ["token"] = 0.5 }; break;
            default:
                request.Parameters["embedding_type"] = JsonSerializer.SerializeToElement(kind);
                request.Parameters["dimensions"] = JsonSerializer.SerializeToElement(16);
                raw["data"]![0]!["embedding"] = kind == "binary" ? new JsonArray(-128, 127) : new JsonArray(0, 255); break;
        }
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var expected = JsonSerializer.SerializeToNode(request)!.AsObject(); expected["model"] = model;
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsStringAsync(token)))); return OpenAiEmbeddingFixtures.Json(raw);
        });
        await using var client = OpenAiEmbeddingFixtures.Build(JinaCompatibilityFixtures.Adapter(handler, model));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Embedding.EmbedAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>互相矛盾的原生编码字段都原样发出，不能删除 encoding_format 来让返回看似兼容。</summary>
    [Fact]
    public async Task ConflictingEncodingFields_AreNotRepaired()
    {
        var request = JinaCompatibilityFixtures.Request("base64"); request.Parameters["encoding_format"] = JsonSerializer.SerializeToElement("float");
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var expected = JinaCompatibilityFixtures.Read("base64", "wire"); expected["encoding_format"] = "float";
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsStringAsync(token)))); return OpenAiEmbeddingFixtures.Json(JinaCompatibilityFixtures.Read("base64"));
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => JinaCompatibilityFixtures.Adapter(handler).EmbedAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Jina nullable dimensions 不等于 OpenAI 原生正整数参数，SDK 不替调用者删除 null。</summary>
    [Fact]
    public async Task NullableDimension_IsNotSilentlyOmitted()
    {
        var request = JinaCompatibilityFixtures.Request(); request.Parameters["dimensions"] = JsonSerializer.SerializeToElement<object?>(null);
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAnyAsync<ArgumentException>(() => JinaCompatibilityFixtures.Adapter(handler).EmbedAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Null, request.Parameters["dimensions"].ValueKind); Assert.Equal(0, handler.Calls);
    }

    /// <summary>未知原生编码的数值数组可能通过通用 shape 校验；不代表 bit-packed 语义已实现。</summary>
    [Theory] [InlineData("binary")] [InlineData("ubinary")]
    public async Task NumericShapeAcceptance_DoesNotTransformNativeBitEncoding(string encoding)
    {
        var request = JinaCompatibilityFixtures.Request(); request.Parameters["embedding_type"] = JsonSerializer.SerializeToElement(encoding);
        var raw = JinaCompatibilityFixtures.Read(); raw["data"]![0]!["embedding"] = encoding == "binary" ? new JsonArray(-128, 127) : new JsonArray(0, 255);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var expected = JinaCompatibilityFixtures.Read("text", "wire"); expected["embedding_type"] = encoding;
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsStringAsync(token)))); return OpenAiEmbeddingFixtures.Json(raw);
        });
        var result = await JinaCompatibilityFixtures.Adapter(handler).EmbedAsync(request, TestContext.Current.CancellationToken);
        Assert.True(JsonNode.DeepEquals(raw, JsonNode.Parse(result.Data.GetRawText())));
        Assert.Equal(2, result.OpenAiVectors.Single().Values!.Count); Assert.Equal(1, handler.Calls);
    }
}
