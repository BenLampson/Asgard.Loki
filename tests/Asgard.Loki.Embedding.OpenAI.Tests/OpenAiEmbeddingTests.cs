using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Embedding.OpenAI.Tests;

/// <summary>通过公共 SDK 和实际供应商适配器验证完整原生请求、结果与索引关联。</summary>
public sealed class OpenAiEmbeddingTests
{
    /// <summary>四种输入与两种编码均使用独立完整请求比较，原始向量次序、用量和未知字段不被改写。</summary>
    [Theory] [MemberData(nameof(OpenAiEmbeddingFixtures.Cases), MemberType = typeof(OpenAiEmbeddingFixtures))]
    public async Task NativeCall_MatchesFullWireGolden(string input, string encoding)
    {
        var count = input is "text" or "tokens" ? 1 : 2;
        var raw = OpenAiEmbeddingFixtures.Read("response-" + count + "-" + encoding);
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("https://embed.invalid/tenant/v1/embeddings", request.RequestUri!.AbsoluteUri);
            Assert.Equal("embedding-key", request.Headers.Authorization!.Parameter); Assert.Equal("Bearer", request.Headers.Authorization.Scheme);
            Assert.Equal("org", Assert.Single(request.Headers.GetValues("OpenAI-Organization")));
            Assert.Equal("project", Assert.Single(request.Headers.GetValues("OpenAI-Project")));
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
            Assert.True(JsonNode.DeepEquals(OpenAiEmbeddingFixtures.Read("wire-" + input + "-" + encoding),
                JsonNode.Parse(await request.Content.ReadAsStringAsync(token))));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(raw.ToJsonString())) };
        });
        await using var client = OpenAiEmbeddingFixtures.Build(OpenAiEmbeddingFixtures.Adapter(handler));
        var source = OpenAiEmbeddingFixtures.Request(input, encoding); var before = JsonSerializer.Serialize(source);
        var result = await client.Embedding.EmbedAsync(source, TestContext.Current.CancellationToken);
        Assert.True(JsonNode.DeepEquals(raw, JsonNode.Parse(result.Data.GetRawText()))); Assert.Equal(before, JsonSerializer.Serialize(source));
        Assert.Equal("public", result.Metadata.PublicModel); Assert.Equal("text-embedding-test", result.Metadata.UpstreamModel);
        Assert.Equal("embedding-one", result.Metadata.ProviderId); Assert.Equal("returned-version", result.Data.GetProperty("model").GetString());
        var vectors = result.OpenAiVectors; Assert.Equal(count, vectors.Count); Assert.Equal(count - 1, vectors[0].Index);
        if (encoding == "base64")
        {
            Assert.Null(vectors[0].Values); Assert.NotNull(vectors[0].Base64); Assert.Equal(8, vectors[0].DecodeBytes().Length);
            Assert.Equal(new byte[] { 0, 0, 128, 63, 0, 0, 0, 192 }, vectors.Last().DecodeBytes().ToArray());
        }
        else
        {
            Assert.Null(vectors[0].Base64); Assert.Equal(2, vectors[0].Values!.Count);
            Assert.Throws<InvalidOperationException>(() => vectors[0].DecodeBytes());
        }
        Assert.Equal(3, result.Data.GetProperty("usage").GetProperty("prompt_tokens").GetInt32()); Assert.Equal(1, handler.Calls);
    }

    /// <summary>编码省略时不插入默认值或自动解码，缺失用量保持未知而不是填零。</summary>
    [Theory] [InlineData("float")] [InlineData("base64")]
    public async Task UnspecifiedEncodingAndUsage_StayNative(string encoding)
    {
        var request = OpenAiEmbeddingFixtures.Request(); request.Parameters.Remove("encoding_format"); request.Parameters.Remove("dimensions");
        var raw = OpenAiEmbeddingFixtures.Read("response-2-" + encoding); raw.Remove("usage");
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var body = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!.AsObject();
            Assert.False(body.ContainsKey("encoding_format")); Assert.False(body.ContainsKey("dimensions")); Assert.False(body.ContainsKey("stream"));
            return OpenAiEmbeddingFixtures.Json(raw);
        });
        await using var client = OpenAiEmbeddingFixtures.Build(OpenAiEmbeddingFixtures.Adapter(handler));
        var result = await client.Embedding.EmbedAsync(request, TestContext.Current.CancellationToken);
        Assert.True(JsonNode.DeepEquals(raw, JsonNode.Parse(result.Data.GetRawText()))); Assert.False(result.Data.TryGetProperty("usage", out _));
    }

    /// <summary>上游 HTTP 错误保留完整正文和状态，即使带 Retry-After 也不重发。</summary>
    [Theory] [InlineData(400)] [InlineData(401)] [InlineData(429)] [InlineData(503)]
    public async Task HttpFailure_PreservesBodyWithoutRetry(int status)
    {
        const string body = " \n{\"error\":{\"code\":\"failed\",\"future\":false}}\n ";
        using var handler = new StubHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
            response.Headers.Add("Retry-After", "1"); return Task.FromResult(response);
        });
        await using var client = OpenAiEmbeddingFixtures.Build(OpenAiEmbeddingFixtures.Adapter(handler));
        var error = await Assert.ThrowsAsync<EmbeddingUpstreamException>(() => client.Embedding.EmbedAsync(OpenAiEmbeddingFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.Equal(body, error.ResponseBody); Assert.Equal(1, handler.Calls);
    }

    /// <summary>HTTP 200 的业务错误也不能变成成功向量或空列表。</summary>
    [Fact]
    public async Task BusinessError_IsNotSuccessfulEmbedding()
    {
        const string body = "\n{\"error\":{\"message\":\"failed\"}}\n";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
        var error = await Assert.ThrowsAsync<EmbeddingUpstreamException>(() => OpenAiEmbeddingFixtures.Adapter(handler)
            .EmbedAsync(OpenAiEmbeddingFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(200, error.StatusCode); Assert.Equal(body, error.ResponseBody); Assert.Equal(1, handler.Calls);
    }

    /// <summary>非 JSON、截断 JSON 及 SSE 都不能被视作完整嵌入结果。</summary>
    [Theory] [InlineData("invalid")] [InlineData("truncated")] [InlineData("sse")] [InlineData("duplicate")]
    public async Task MalformedTransport_DoesNotBecomeSuccess(string kind)
    {
        using var handler = new StubHttpHandler((_, _) =>
        {
            var content = new StringContent(kind switch { "invalid" => "not-json", "truncated" => "{\"data\":[", "duplicate" => "{\"data\":[],\"data\":[]}", _ => "data: {}\n\n" }, Encoding.UTF8,
                kind == "sse" ? "text/event-stream" : "application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        var adapter = OpenAiEmbeddingFixtures.Adapter(handler);
        if (kind == "sse") await Assert.ThrowsAsync<InvalidDataException>(() => adapter.EmbedAsync(OpenAiEmbeddingFixtures.Request(), TestContext.Current.CancellationToken));
        else await Assert.ThrowsAnyAsync<JsonException>(() => adapter.EmbedAsync(OpenAiEmbeddingFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }
}
