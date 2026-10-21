using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Embedding.OpenAI.Tests;

/// <summary>显式账户范围的 Cloudflare 标准嵌入协议复用，不复制聊天转换或原生 run 包装。</summary>
public sealed class CloudflareCompatibilityTests
{
    /// <summary>完整账户/代理前缀、原生模型、Bearer、文本与原始向量索引独立保留。</summary>
    [Theory]
    [InlineData(0, true)] [InlineData(0, false)] [InlineData(1, true)] [InlineData(1, false)]
    public async Task AccountScopedCanonicalWireAndResultsRemainNative(int index, bool trailingSlash)
    {
        var original = CloudflareProfileFixture.Options();
        var options = new OpenAiEmbeddingOptions
        {
            Id = original.Id, Model = original.Model, ApiKey = original.ApiKey, HttpClientName = original.HttpClientName,
            BaseAddress = new(trailingSlash ? original.BaseAddress.AbsoluteUri : original.BaseAddress.AbsoluteUri.TrimEnd('/'))
        };
        using var factory = CloudflareProfileFixture.Factory(index); factory.OnSend = async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal(CloudflareProfileFixture.Part("profile").GetProperty("endpoint").GetString(), message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(options.ApiKey, message.Headers.Authorization.Parameter);
            Assert.False(message.Headers.Contains("OpenAI-Organization")); Assert.False(message.Headers.Contains("OpenAI-Project")); Assert.False(message.Headers.Contains("api-key"));
            var wire = CloudflareProfileFixture.Element(await message.Content!.ReadAsStringAsync(token));
            Assert.True(JsonElement.DeepEquals(CloudflareProfileFixture.Part("requests")[index], wire));
            Assert.False(wire.TryGetProperty("encoding_format", out _)); Assert.False(wire.TryGetProperty("dimensions", out _));
            Assert.False(wire.TryGetProperty("text", out _)); Assert.False(wire.TryGetProperty("stream", out _));
            return CloudflareProfileFixture.Json(CloudflareProfileFixture.Part("responses")[index]);
        };
        await using var client = new LokiClientBuilder().AddEmbedding(new() { Routes = [new() { Model = "vectors", Targets = [new OpenAiEmbeddingAdapter(factory, options)] }] }).Build();
        var result = await client.Embedding.EmbedAsync(CloudflareProfileFixture.Request(index), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(CloudflareProfileFixture.Part("responses")[index], result.Data)); Assert.Equal(new EmbeddingResultMetadata("vectors", options.Model, options.Id), result.Metadata);
        Assert.Equal("9007199254740993", result.Data.GetProperty("usage").GetProperty("future").GetRawText());
        Assert.Equal("0.123456789012345678901", result.Data.GetProperty("data")[index == 0 ? 1 : 0].GetProperty("embedding")[0].GetRawText());
        if (index == 0) { Assert.Equal(1, result.OpenAiVectors[0].Index); Assert.Equal("@cf/baai/bge-large-en-v1.5-native-version", result.Data.GetProperty("model").GetString()); }
        else { Assert.False(result.Data.TryGetProperty("object", out _)); Assert.False(result.Data.TryGetProperty("model", out _)); Assert.False(result.Data.GetProperty("usage").TryGetProperty("total_tokens", out _)); }
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed); Assert.Equal(options.HttpClientName, factory.ClientName);
    }

    /// <summary>HTTP 原生账户错误与兼容端点的业务错误均保留原文/类型/实际状态且不重试。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AccountAndBusinessErrorsKeepCompleteNativePayload(bool business)
    {
        var raw = CloudflareProfileFixture.Part(business ? "business_error" : "http_error");
        var status = business ? HttpStatusCode.OK : HttpStatusCode.Forbidden;
        using var factory = CloudflareProfileFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(CloudflareProfileFixture.Json(raw, status));
        var error = await Assert.ThrowsAsync<EmbeddingUpstreamException>(() => new OpenAiEmbeddingAdapter(factory, CloudflareProfileFixture.Options()).EmbedAsync(CloudflareProfileFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal((int)status, error.StatusCode); Assert.Equal(Encoding.UTF8.GetBytes(raw.GetRawText()), error.Body.ToArray());
        Assert.Equal("application/json; charset=utf-8", error.ContentType); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }

    /// <summary>不同 ai/run 操作的 shape/data 包装不能按位置伪造规范 index 或空向量成功。</summary>
    [Fact]
    public async Task NativeRunEnvelopeIsRejectedWithoutFlatteningOrInventedIndices()
    {
        using var factory = CloudflareProfileFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(CloudflareProfileFixture.Json(CloudflareProfileFixture.Part("native_run_envelope")));
        await Assert.ThrowsAsync<InvalidDataException>(() => new OpenAiEmbeddingAdapter(factory, CloudflareProfileFixture.Options()).EmbedAsync(CloudflareProfileFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
}
