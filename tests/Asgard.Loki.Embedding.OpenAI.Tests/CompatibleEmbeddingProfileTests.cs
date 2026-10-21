using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Embedding.OpenAI.Tests;

/// <summary>通过公共 SDK 验证显式配置的标准文本嵌入目标，不推断托管服务或模型可用性。</summary>
public sealed class CompatibleEmbeddingProfileTests
{
    /// <summary>完整目标、模型、凭据与原始文本及索引保持，返回模型不覆盖为配置模型。</summary>
    [Theory] [MemberData(nameof(CompatibleEmbeddingProfiles.Calls), MemberType = typeof(CompatibleEmbeddingProfiles))]
    public async Task ExplicitNativeTextProfilesPreserveWholeWireAndResult(int index, bool batch)
    {
        var options = CompatibleEmbeddingProfiles.Options(index); var expected = CompatibleEmbeddingProfiles.Part(batch ? "batch_response" : "scalar_response");
        using var factory = new EmbeddingTransportFactory(); factory.OnSend = async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal(CompatibleEmbeddingProfiles.Part("profiles")[index].GetProperty("endpoint").GetString(), message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(options.ApiKey, message.Headers.Authorization.Parameter);
            Assert.False(message.Headers.Contains("OpenAI-Organization")); Assert.False(message.Headers.Contains("OpenAI-Project")); Assert.False(message.Headers.Contains("api-key"));
            var wire = M3eProfileFixture.Element(await message.Content!.ReadAsStringAsync(token)); Assert.True(JsonElement.DeepEquals(CompatibleEmbeddingProfiles.Wire(index, batch), wire));
            Assert.Equal(options.Model, wire.GetProperty("model").GetString()); Assert.Equal("9007199254740993", wire.GetProperty("future").GetProperty("exact").GetRawText());
            Assert.False(wire.TryGetProperty("encoding_format", out _)); Assert.False(wire.TryGetProperty("dimensions", out _)); Assert.False(wire.TryGetProperty("stream", out _));
            return M3eProfileFixture.Json(expected);
        };
        await using var client = new LokiClientBuilder().AddEmbedding(new() { Routes = [new() { Model = "public-vectors", Targets = [new OpenAiEmbeddingAdapter(factory, options)] }] }).Build();
        var request = CompatibleEmbeddingProfiles.Request(index, batch); var before = JsonSerializer.Serialize(request);
        var result = await client.Embedding.EmbedAsync(request, TestContext.Current.CancellationToken); Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.True(JsonElement.DeepEquals(expected, result.Data)); Assert.Equal(new EmbeddingResultMetadata(request.Model, options.Model, options.Id), result.Metadata);
        Assert.Equal(batch ? 1 : 0, result.OpenAiVectors[0].Index); Assert.Equal("native-reported-version", result.Data.GetProperty("model").GetString());
        Assert.Equal("0.123456789012345678901", result.Data.GetProperty("data")[batch ? 1 : 0].GetProperty("embedding")[0].GetRawText());
        Assert.Equal("9007199254740993", result.Data.GetProperty("usage").GetProperty("future").GetRawText()); Assert.False(result.Data.GetProperty("usage").TryGetProperty("total_tokens", out _));
        Assert.Equal(1, factory.Calls); Assert.Equal(options.HttpClientName, factory.ClientName); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }
    /// <summary>每个独立目标的拒绝正文、媒体类型和真实状态保留，即使带重试建议也只发送一次。</summary>
    [Theory] [MemberData(nameof(CompatibleEmbeddingProfiles.Targets), MemberType = typeof(CompatibleEmbeddingProfiles))]
    public async Task ExplicitProfileErrorsStayNativeWithoutRetry(int index)
    {
        var raw = CompatibleEmbeddingProfiles.Part("http_error"); using var factory = new EmbeddingTransportFactory(); factory.OnSend = (_, _) =>
        { var response = M3eProfileFixture.Json(raw, HttpStatusCode.TooManyRequests); response.Headers.TryAddWithoutValidation("Retry-After", "1"); return Task.FromResult(response); };
        var error = await Assert.ThrowsAsync<EmbeddingUpstreamException>(() => new OpenAiEmbeddingAdapter(factory, CompatibleEmbeddingProfiles.Options(index)).EmbedAsync(CompatibleEmbeddingProfiles.Request(index, true), TestContext.Current.CancellationToken));
        Assert.Equal(429, error.StatusCode); Assert.Equal(Encoding.UTF8.GetBytes(raw.GetRawText()), error.Body.ToArray()); Assert.Equal(raw.GetRawText(), error.ResponseBody);
        Assert.Equal("application/json; charset=utf-8", error.ContentType); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }
}
