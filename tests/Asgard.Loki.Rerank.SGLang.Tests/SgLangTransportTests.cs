using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Rerank.SGLang.Tests;

/// <summary>本地原始字节、编码、目标认证和释放后取消边界，不依赖真实兼容服务。</summary>
public sealed class SgLangTransportTests
{
    /// <summary>HTTP 二进制错误保留字节和内容类型，既不替换字符也不重新发送。</summary>
    [Fact]
    public async Task BinaryErrorKeepsOriginalBytesAndContentType()
    {
        byte[] bytes = [255, 0, 254]; using var factory = SgLangFixture.Factory(); factory.OnSend = (_, _) =>
        { var content = new ByteArrayContent(bytes); content.Headers.ContentType = new("application/octet-stream"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = content }); };
        var error = await Assert.ThrowsAsync<RerankUpstreamException>(() => new SgLangRerankAdapter(factory, SgLangFixture.Options()).RerankAsync(SgLangFixture.Request(1), TestContext.Current.CancellationToken));
        Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal("application/octet-stream", error.ContentType); Assert.Throws<DecoderFallbackException>(() => error.ResponseBody); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>未知字段中的坏编码也不能被解码器有损替换，原始字节和声明长度均有界。</summary>
    [Theory]
    [InlineData("limit")] [InlineData("length")] [InlineData("utf8")] [InlineData("unicode")] [InlineData("charset")] [InlineData("type")] [InlineData("invalid-type")]
    public async Task FullRawResponseMustMeetByteEncodingAndMediaContract(string kind)
    {
        var bytes = Encoding.UTF8.GetBytes(SgLangFixture.Part("responses")[1].GetRawText());
        if (kind == "utf8") bytes = Encoding.UTF8.GetBytes("[{\"index\":1,\"score\":0,\"unknown\":\"").Concat(new byte[] { 255 }).Concat(Encoding.UTF8.GetBytes("\"},{\"index\":0,\"score\":-1}]")).ToArray();
        if (kind == "unicode") bytes = Encoding.UTF8.GetBytes("[{\"index\":1,\"score\":0,\"unknown\":\"\\ud800\"},{\"index\":0,\"score\":-1}]");
        using var source = new SgLangReadStream(bytes); using var factory = SgLangFixture.Factory(); factory.OnSend = (_, _) =>
        {
            var content = new StreamContent(source); content.Headers.ContentType = new(kind == "type" ? "text/plain" : "application/json");
            if (kind == "invalid-type") { content.Headers.Remove("Content-Type"); content.Headers.TryAddWithoutValidation("Content-Type", "not a media type"); }
            if (kind == "charset") content.Headers.ContentType!.CharSet = "utf-16"; if (kind == "length") content.Headers.ContentLength = bytes.Length + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => new SgLangRerankAdapter(factory, SgLangFixture.Options(responseLimit: kind == "limit" ? bytes.Length - 1 : bytes.Length + 100)).RerankAsync(SgLangFixture.Request(1), TestContext.Current.CancellationToken));
        Assert.True(source.Disposed); Assert.Equal(1, factory.Disposals); Assert.Equal(1, factory.Calls);
    }
    /// <summary>完整未知成员中的重复字段抛出 JsonException，不能用末值掩盖歧义。</summary>
    [Fact]
    public async Task DuplicateUnknownFieldsCannotHideAmbiguity()
    {
        var bytes = Encoding.UTF8.GetBytes("[{\"index\":1,\"score\":0,\"future\":{\"a\":1,\"a\":2}},{\"index\":0,\"score\":-1}]");
        using var factory = SgLangFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        await Assert.ThrowsAnyAsync<JsonException>(() => new SgLangRerankAdapter(factory, SgLangFixture.Options()).RerankAsync(SgLangFixture.Request(1), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>请求上限、无效 CLR Unicode 和固定模型大小写覆盖在工厂前失败。</summary>
    [Fact]
    public async Task InvalidRequestsCannotCreateHttpResources()
    {
        using var factory = SgLangFixture.Factory();
        await Assert.ThrowsAsync<ArgumentException>(() => new SgLangRerankAdapter(factory, SgLangFixture.Options(requestLimit: 1)).RerankAsync(SgLangFixture.Request(1), TestContext.Current.CancellationToken));
        var request = SgLangFixture.Request(1) with { Model = "\ud800" };
        await Assert.ThrowsAsync<EncoderFallbackException>(() => new SgLangRerankAdapter(factory, SgLangFixture.Options()).RerankAsync(request, TestContext.Current.CancellationToken));
        request = SgLangFixture.Request(1); request.Parameters["MODEL"] = JsonSerializer.SerializeToElement("override");
        await Assert.ThrowsAsync<ArgumentException>(() => new SgLangRerankAdapter(factory, SgLangFixture.Options()).RerankAsync(request, TestContext.Current.CancellationToken)); Assert.Equal(0, factory.Creates);
    }
    /// <summary>编码和字节恰在上限时仍成功，不以字符数或宽松大于等于比较误拒绝。</summary>
    [Fact]
    public async Task ExactByteLimitPreservesNativeJsonAndPrecision()
    {
        var body = SgLangFixture.Part("responses")[1]; var bytes = Encoding.UTF8.GetBytes(body.GetRawText()); using var source = new SgLangReadStream(bytes);
        using var factory = SgLangFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) });
        var result = await new SgLangRerankAdapter(factory, SgLangFixture.Options(responseLimit: bytes.Length)).RerankAsync(SgLangFixture.Request(1), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(body, result.Data)); Assert.True(source.Eof); Assert.True(source.Disposed); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>固定目标的认证和组织/项目配置不会与工厂其他能力的默认头部混合。</summary>
    [Fact]
    public async Task TargetHeadersRemainIndependentOfFactoryDefaults()
    {
        using var factory = SgLangFixture.Factory(); factory.ConfigureClient = client =>
        { client.DefaultRequestHeaders.Authorization = new("Bearer", "unrelated-key"); client.DefaultRequestHeaders.Add("api-key", "unrelated-key"); client.DefaultRequestHeaders.Add("OpenAI-Organization", "unrelated-org"); client.DefaultRequestHeaders.Add("OpenAI-Project", "unrelated-project"); };
        factory.OnSend = (message, _) =>
        { Assert.Equal(SgLangFixture.Options().ApiKey, message.Headers.Authorization!.Parameter); Assert.False(message.Headers.Contains("api-key")); Assert.False(message.Headers.Contains("OpenAI-Organization")); Assert.False(message.Headers.Contains("OpenAI-Project")); return Task.FromResult(SgLangFixture.Json(SgLangFixture.Part("responses")[1])); };
        await new SgLangRerankAdapter(factory, SgLangFixture.Options()).RerankAsync(SgLangFixture.Request(1), TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls);
    }
    /// <summary>处理器改变实际目标或方法时不能发布其他服务器的向量。</summary>
    [Fact]
    public async Task ChangedActualTargetFailsBeforeReadingResponse()
    {
        using var source = new SgLangReadStream(Encoding.UTF8.GetBytes(SgLangFixture.Part("responses")[1].GetRawText())); using var actual = new HttpRequestMessage(HttpMethod.Post, "https://other.example.invalid/rerank");
        using var factory = SgLangFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source), RequestMessage = actual });
        await Assert.ThrowsAsync<InvalidDataException>(() => new SgLangRerankAdapter(factory, SgLangFixture.Options()).RerankAsync(SgLangFixture.Request(1), TestContext.Current.CancellationToken)); Assert.False(source.Eof); Assert.True(source.Disposed); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>读完数据后发生的读取/流/客户端释放/取消失败不能先交付成功结果。</summary>
    [Theory]
    [InlineData("read")] [InlineData("dispose")] [InlineData("client")] [InlineData("cancel")]
    public async Task LateFailuresPreventSuccessAfterNativeBodyWasRead(string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var source = new SgLangReadStream(Encoding.UTF8.GetBytes(SgLangFixture.Part("responses")[1].GetRawText())) { FailRead = kind == "read", FailDispose = kind == "dispose" };
        using var factory = SgLangFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) });
        if (kind == "client") factory.OnDisposeClient = () => throw new IOException("fixture client disposal"); if (kind == "cancel") factory.OnDisposeClient = cancellation.Cancel;
        if (kind == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SgLangRerankAdapter(factory, SgLangFixture.Options()).RerankAsync(SgLangFixture.Request(1), cancellation.Token));
        else await Assert.ThrowsAsync<IOException>(() => new SgLangRerankAdapter(factory, SgLangFixture.Options()).RerankAsync(SgLangFixture.Request(1), cancellation.Token));
        Assert.True(source.Disposed); Assert.Equal(1, factory.Disposals); Assert.Equal(1, factory.Calls); Assert.False(factory.Disposed);
    }
}
