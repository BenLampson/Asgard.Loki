using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Embedding.OpenAI.Tests;

/// <summary>本地原始字节、编码、目标认证和释放后取消边界，不依赖真实兼容服务。</summary>
public sealed class EmbeddingTransportBoundaryTests
{
    /// <summary>HTTP 二进制错误保留字节和内容类型，既不替换字符也不重新发送。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task BinaryErrorKeepsOriginalBytesAndContentType(bool malformedType)
    {
        byte[] bytes = [255, 0, 254]; using var factory = M3eProfileFixture.Factory(); factory.OnSend = (_, _) =>
        { var content = new ByteArrayContent(bytes); content.Headers.ContentType = new("application/octet-stream"); if (malformedType) { content.Headers.Remove("Content-Type"); content.Headers.TryAddWithoutValidation("Content-Type", "not a media type"); } return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = content }); };
        var error = await Assert.ThrowsAsync<EmbeddingUpstreamException>(() => new OpenAiEmbeddingAdapter(factory, M3eProfileFixture.Options()).EmbedAsync(M3eProfileFixture.Request(1), TestContext.Current.CancellationToken));
        Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal(malformedType ? "not a media type" : "application/octet-stream", error.ContentType); Assert.Throws<DecoderFallbackException>(() => error.ResponseBody); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>未知字段中的坏编码也不能被解码器有损替换，原始字节和声明长度均有界。</summary>
    [Theory]
    [InlineData("limit")] [InlineData("utf8")] [InlineData("unicode")] [InlineData("charset")] [InlineData("type")] [InlineData("invalid-type")]
    public async Task FullRawResponseMustMeetByteEncodingAndMediaContract(string kind)
    {
        var bytes = Encoding.UTF8.GetBytes(M3eProfileFixture.Part("responses")[1].GetRawText());
        if (kind == "utf8") bytes = Encoding.UTF8.GetBytes("{\"data\":[{\"index\":0,\"embedding\":[0,1]}],\"unknown\":\"").Concat(new byte[] { 255 }).Concat(Encoding.UTF8.GetBytes("\"}")).ToArray();
        if (kind == "unicode") bytes = Encoding.UTF8.GetBytes("{\"data\":[{\"index\":0,\"embedding\":[0,1]}],\"unknown\":\"\\ud800\"}");
        using var source = new EmbeddingTransportReadStream(bytes); using var factory = M3eProfileFixture.Factory(); factory.OnSend = (_, _) =>
        {
            var content = new StreamContent(source); content.Headers.ContentType = new(kind == "type" ? "text/plain" : "application/json");
            if (kind == "invalid-type") { content.Headers.Remove("Content-Type"); content.Headers.TryAddWithoutValidation("Content-Type", "not a media type"); }
            if (kind == "charset") content.Headers.ContentType!.CharSet = "utf-16";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => new OpenAiEmbeddingAdapter(factory, M3eProfileFixture.Options(responseLimit: kind == "limit" ? bytes.Length - 1 : bytes.Length + 100)).EmbedAsync(M3eProfileFixture.Request(1), TestContext.Current.CancellationToken));
        Assert.True(source.Disposed); Assert.Equal(1, factory.Disposals); Assert.Equal(1, factory.Calls);
    }
    /// <summary>完整未知成员中的重复字段保留既有 JsonException 契约，不能用末值掩盖歧义。</summary>
    [Fact]
    public async Task DuplicateUnknownFieldsRetainExistingJsonFailureContract()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"data\":[{\"index\":0,\"embedding\":[0,1]}],\"future\":{\"a\":1,\"a\":2}}");
        using var factory = M3eProfileFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        await Assert.ThrowsAnyAsync<JsonException>(() => new OpenAiEmbeddingAdapter(factory, M3eProfileFixture.Options()).EmbedAsync(M3eProfileFixture.Request(1), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>请求上限、无效 CLR Unicode 和固定模型大小写覆盖在工厂前失败。</summary>
    [Fact]
    public async Task InvalidRequestsCannotCreateHttpResources()
    {
        using var factory = M3eProfileFixture.Factory();
        await Assert.ThrowsAsync<ArgumentException>(() => new OpenAiEmbeddingAdapter(factory, M3eProfileFixture.Options(requestLimit: 1)).EmbedAsync(M3eProfileFixture.Request(1), TestContext.Current.CancellationToken));
        var request = M3eProfileFixture.Request(1) with { Model = "\ud800" };
        await Assert.ThrowsAsync<EncoderFallbackException>(() => new OpenAiEmbeddingAdapter(factory, M3eProfileFixture.Options()).EmbedAsync(request, TestContext.Current.CancellationToken));
        request = M3eProfileFixture.Request(1); request.Parameters["MODEL"] = JsonSerializer.SerializeToElement("override");
        await Assert.ThrowsAsync<ArgumentException>(() => new OpenAiEmbeddingAdapter(factory, M3eProfileFixture.Options()).EmbedAsync(request, TestContext.Current.CancellationToken)); Assert.Equal(0, factory.Creates);
    }
    /// <summary>编码和字节恰在上限时仍成功，不以字符数或宽松大于等于比较误拒绝。</summary>
    [Fact]
    public async Task ExactByteLimitPreservesNativeJsonAndPrecision()
    {
        var body = M3eProfileFixture.Part("responses")[1]; var bytes = Encoding.UTF8.GetBytes(body.GetRawText()); using var source = new EmbeddingTransportReadStream(bytes);
        using var factory = M3eProfileFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) });
        var result = await new OpenAiEmbeddingAdapter(factory, M3eProfileFixture.Options(responseLimit: bytes.Length)).EmbedAsync(M3eProfileFixture.Request(1), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(body, result.Data)); Assert.True(source.Eof); Assert.True(source.Disposed); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>固定目标的认证和组织/项目配置不会与工厂其他能力的默认头部混合。</summary>
    [Fact]
    public async Task TargetHeadersRemainIndependentOfFactoryDefaults()
    {
        using var factory = M3eProfileFixture.Factory(); factory.ConfigureClient = client =>
        { client.DefaultRequestHeaders.Authorization = new("Bearer", "unrelated-key"); client.DefaultRequestHeaders.Add("api-key", "unrelated-key"); client.DefaultRequestHeaders.Add("OpenAI-Organization", "unrelated-org"); client.DefaultRequestHeaders.Add("OpenAI-Project", "unrelated-project"); };
        factory.OnSend = (message, _) =>
        { Assert.Equal(M3eProfileFixture.Options().ApiKey, message.Headers.Authorization!.Parameter); Assert.False(message.Headers.Contains("api-key")); Assert.False(message.Headers.Contains("OpenAI-Organization")); Assert.False(message.Headers.Contains("OpenAI-Project")); return Task.FromResult(M3eProfileFixture.Json(M3eProfileFixture.Part("responses")[1])); };
        await new OpenAiEmbeddingAdapter(factory, M3eProfileFixture.Options()).EmbedAsync(M3eProfileFixture.Request(1), TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls);
    }
    /// <summary>处理器改变实际目标或方法时不能发布其他服务器的向量。</summary>
    [Fact]
    public async Task ChangedActualTargetFailsBeforeReadingResponse()
    {
        using var source = new EmbeddingTransportReadStream(Encoding.UTF8.GetBytes(M3eProfileFixture.Part("responses")[1].GetRawText())); using var actual = new HttpRequestMessage(HttpMethod.Post, "https://other.example.invalid/embeddings");
        using var factory = M3eProfileFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source), RequestMessage = actual });
        await Assert.ThrowsAsync<InvalidDataException>(() => new OpenAiEmbeddingAdapter(factory, M3eProfileFixture.Options()).EmbedAsync(M3eProfileFixture.Request(1), TestContext.Current.CancellationToken)); Assert.False(source.Eof); Assert.True(source.Disposed); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>公共读取/流关闭矩阵由 Infrastructure.Tests 覆盖；客户端关闭和最终取消仍由供应商验证。</summary>
    [Theory]
    [InlineData("client")] [InlineData("cancel")]
    public async Task LateFailuresPreventSuccessAfterNativeBodyWasRead(string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var source = new EmbeddingTransportReadStream(Encoding.UTF8.GetBytes(M3eProfileFixture.Part("responses")[1].GetRawText()));
        using var factory = M3eProfileFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) });
        if (kind == "client") factory.OnDisposeClient = () => throw new IOException("fixture client disposal"); if (kind == "cancel") factory.OnDisposeClient = cancellation.Cancel;
        if (kind == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OpenAiEmbeddingAdapter(factory, M3eProfileFixture.Options()).EmbedAsync(M3eProfileFixture.Request(1), cancellation.Token));
        else await Assert.ThrowsAsync<IOException>(() => new OpenAiEmbeddingAdapter(factory, M3eProfileFixture.Options()).EmbedAsync(M3eProfileFixture.Request(1), cancellation.Token));
        Assert.True(source.Disposed); Assert.Equal(1, factory.Disposals); Assert.Equal(1, factory.Calls); Assert.False(factory.Disposed);
    }
}
