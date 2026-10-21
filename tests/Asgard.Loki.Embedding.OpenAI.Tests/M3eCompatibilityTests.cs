using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Embedding.OpenAI.Tests;

/// <summary>显式规范 M3E 文本嵌入目标复用，完整保留请求、原生索引和模型/用量/未知字段。</summary>
public sealed class M3eCompatibilityTests
{
    /// <summary>文本和批输入不被强制改写，返回模型不替换为虚构别名，重排向量按原生 index 保留。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public async Task CanonicalM3eWireAndResultsRemainOriginal(int index)
    {
        var options = M3eProfileFixture.Options(); using var factory = M3eProfileFixture.Factory(index); factory.OnSend = async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://m3e-embedding.example.invalid/prefix/embeddings", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(options.ApiKey, message.Headers.Authorization.Parameter);
            Assert.False(message.Headers.Contains("OpenAI-Organization")); Assert.False(message.Headers.Contains("OpenAI-Project"));
            Assert.True(JsonElement.DeepEquals(M3eProfileFixture.Part("requests")[index], M3eProfileFixture.Element(await message.Content!.ReadAsStringAsync(token))));
            return M3eProfileFixture.Json(M3eProfileFixture.Part("responses")[index]);
        };
        await using var client = new LokiClientBuilder().AddEmbedding(new() { Routes = [new() { Model = "vectors", Targets = [new OpenAiEmbeddingAdapter(factory, options)] }] }).Build();
        var result = await client.Embedding.EmbedAsync(M3eProfileFixture.Request(index), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(M3eProfileFixture.Part("responses")[index], result.Data)); Assert.Equal(new EmbeddingResultMetadata("vectors", options.Model, options.Id), result.Metadata);
        Assert.Equal("9007199254740993", result.Data.GetProperty("usage").GetProperty("future").GetRawText());
        Assert.Equal("0.123456789012345678901", result.Data.GetProperty("data")[index == 0 ? 1 : 0].GetProperty("embedding")[0].GetRawText());
        if (index == 0) { Assert.Equal(1, result.OpenAiVectors[0].Index); Assert.Equal("native-m3e-reported-version", result.Data.GetProperty("model").GetString()); }
        else { Assert.False(result.Data.TryGetProperty("object", out _)); Assert.False(result.Data.TryGetProperty("model", out _)); Assert.False(result.Data.GetProperty("usage").TryGetProperty("total_tokens", out _)); }
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed); Assert.Equal(options.HttpClientName, factory.ClientName);
    }
    /// <summary>独立 native 参数在工厂前冻结，后续清空不能改变既定请求。</summary>
    [Fact]
    public async Task CanonicalProfileSnapshotPrecedesFactory()
    {
        var request = M3eProfileFixture.Request(); using var factory = M3eProfileFixture.Factory(); factory.OnCreate = request.Parameters.Clear;
        factory.OnSend = async (message, token) =>
        { Assert.True(JsonElement.DeepEquals(M3eProfileFixture.Part("requests")[0], M3eProfileFixture.Element(await message.Content!.ReadAsStringAsync(token)))); return M3eProfileFixture.Json(M3eProfileFixture.Part("responses")[0]); };
        await new OpenAiEmbeddingAdapter(factory, M3eProfileFixture.Options()).EmbedAsync(request, TestContext.Current.CancellationToken); Assert.Empty(request.Parameters); Assert.Equal(1, factory.Calls);
    }
    /// <summary>原生 HTTP 错误原文、状态与未知字段保持，不因目标标记为兼容而重试。</summary>
    [Fact]
    public async Task CanonicalProfileHttpErrorsRemainWhole()
    {
        using var factory = M3eProfileFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(M3eProfileFixture.Json(M3eProfileFixture.Part("http_error"), HttpStatusCode.BadRequest));
        var error = await Assert.ThrowsAsync<EmbeddingUpstreamException>(() => new OpenAiEmbeddingAdapter(factory, M3eProfileFixture.Options()).EmbedAsync(M3eProfileFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(400, error.StatusCode); Assert.True(JsonElement.DeepEquals(M3eProfileFixture.Part("http_error"), M3eProfileFixture.Element(error.ResponseBody))); Assert.Equal(1, factory.Calls);
    }
    /// <summary>兼容配置不能以数组位置掩盖原生重复 index。</summary>
    [Fact]
    public async Task CanonicalProfileRejectsDuplicateAssociationWithoutRepair()
    {
        var data = JsonNode.Parse(M3eProfileFixture.Part("responses")[0].GetRawText())!.AsObject(); data["data"]![1]!["index"] = 1;
        using var factory = M3eProfileFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(M3eProfileFixture.Json(M3eProfileFixture.Element(data.ToJsonString())));
        await Assert.ThrowsAsync<InvalidDataException>(() => new OpenAiEmbeddingAdapter(factory, M3eProfileFixture.Options()).EmbedAsync(M3eProfileFixture.Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls);
    }
}
