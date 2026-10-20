using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Embedding.Azure.Tests;

/// <summary>原生错误、输入/目标验证、完整 HTTP 字节及最终资源边界。</summary>
public sealed class AzureEmbeddingFailureTests
{
    /// <summary>实际状态和完整原生 API 错误保持，HTTP 200 也不能伪装成功。</summary>
    [Theory]
    [InlineData(200)] [InlineData(400)]
    public async Task NativeApiErrorsPreserveActualStatusAndCompleteBody(int status)
    {
        using var factory = AzureEmbeddingFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(AzureEmbeddingFixture.Json(AzureEmbeddingFixture.Part("api_error"), (HttpStatusCode)status));
        var error = await Assert.ThrowsAsync<EmbeddingUpstreamException>(() => new AzureEmbeddingAdapter(factory, AzureEmbeddingFixture.Options()).EmbedAsync(AzureEmbeddingFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.True(JsonElement.DeepEquals(AzureEmbeddingFixture.Part("api_error"), AzureEmbeddingFixture.Element(error.ResponseBody))); Assert.Contains("application/json", error.ContentType);
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }
    /// <summary>HTTP 二进制错误不先执行 JSON/UTF-8 有损解码。</summary>
    [Fact]
    public async Task BinaryHttpErrorKeepsOriginalBytes()
    {
        byte[] bytes = [255, 0, 254]; using var factory = AzureEmbeddingFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new ByteArrayContent(bytes) });
        var error = await Assert.ThrowsAsync<EmbeddingUpstreamException>(() => new AzureEmbeddingAdapter(factory, AzureEmbeddingFixture.Options()).EmbedAsync(AzureEmbeddingFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(bytes, error.Body.ToArray()); Assert.Throws<DecoderFallbackException>(() => error.ResponseBody); Assert.Equal(502, error.StatusCode); Assert.Equal(1, factory.Calls);
    }
    /// <summary>处理器改写响应目标后，不能把其他资源的向量交付为当前部署结果。</summary>
    [Fact]
    public async Task ChangedResponseTargetFailsBeforeBodyIsRead()
    {
        using var source = new AzureEmbeddingReadStream(Encoding.UTF8.GetBytes(AzureEmbeddingFixture.Part("float_response").GetRawText()));
        using var actual = new HttpRequestMessage(HttpMethod.Post, "https://other-resource.example.invalid/openai/deployments/other/embeddings?api-version=2024-10-21");
        using var factory = AzureEmbeddingFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = actual, Content = new StreamContent(source) });
        await Assert.ThrowsAsync<InvalidDataException>(() => new AzureEmbeddingAdapter(factory, AzureEmbeddingFixture.Options()).EmbedAsync(AzureEmbeddingFixture.Request(), TestContext.Current.CancellationToken));
        Assert.False(source.Eof); Assert.True(source.Disposed); Assert.Equal(1, factory.Disposals); Assert.Equal(1, factory.Calls);
    }
    /// <summary>坏输入不分词/修复，不创建 HTTP 客户端。</summary>
    [Theory]
    [InlineData("\"\"")] [InlineData("[]")] [InlineData("[\"text\",1]")]
    [InlineData("[-1]")] [InlineData("[[]]")] [InlineData("{}")]
    public void InvalidNativeInputFailsLocally(string input)
    {
        using var factory = AzureEmbeddingFixture.Factory(); var request = AzureEmbeddingFixture.Request(); request.Parameters["input"] = AzureEmbeddingFixture.Element(input);
        Assert.Throws<ArgumentException>(() => { _ = new AzureEmbeddingAdapter(factory, AzureEmbeddingFixture.Options()).EmbedAsync(request, TestContext.Current.CancellationToken); }); Assert.Equal(0, factory.Creates);
    }
    /// <summary>固定 model/原生字段不得由大小写覆盖，维度和编码必须是明确有效原生类型。</summary>
    [Theory]
    [InlineData("MODEL", "\"override\"")] [InlineData("Input", "\"override\"")]
    [InlineData("dimensions", "0")] [InlineData("encoding_format", "\"hex\"")]
    public void InvalidOverridesAndSettingsFailBeforeHttp(string name, string value)
    {
        using var factory = AzureEmbeddingFixture.Factory(); var request = AzureEmbeddingFixture.Request(); request.Parameters[name] = AzureEmbeddingFixture.Element(value);
        Assert.Throws<ArgumentException>(() => { _ = new AzureEmbeddingAdapter(factory, AzureEmbeddingFixture.Options()).EmbedAsync(request, TestContext.Current.CancellationToken); }); Assert.Equal(0, factory.Creates);
    }
    /// <summary>部署、版本、凭据和资源根不能变成其他目标或隐式默认值。</summary>
    [Theory]
    [InlineData("deployment")] [InlineData("version")] [InlineData("key")] [InlineData("root")]
    public void InvalidCompleteTargetFailsBeforeFactory(string kind)
    {
        using var factory = AzureEmbeddingFixture.Factory(); var options = AzureEmbeddingFixture.Options(deployment: kind == "deployment" ? "../other" : "embed.deploy-01", version: kind == "version" ? "" : "2024-10-21", key: kind == "key" ? "bad\nkey" : "fixture", endpoint: kind == "root" ? "https://resource.example.invalid/?other=1" : "https://resource.example.invalid/");
        Assert.Throws<ArgumentException>(() => new AzureEmbeddingAdapter(factory, options)); Assert.Equal(0, factory.Creates);
    }
    /// <summary>完整请求字节上限在任何工厂或等待之前检查。</summary>
    [Fact]
    public void RequestByteLimitFailsLocally()
    {
        using var factory = AzureEmbeddingFixture.Factory(); Assert.Throws<ArgumentException>(() => { _ = new AzureEmbeddingAdapter(factory, AzureEmbeddingFixture.Options(requestLimit: 1)).EmbedAsync(AzureEmbeddingFixture.Request(), TestContext.Current.CancellationToken); }); Assert.Equal(0, factory.Creates);
    }
    /// <summary>完整正文需满足字节/长度/编码/唯一成员/媒体类型约束。</summary>
    [Theory]
    [InlineData("bytes")] [InlineData("length")] [InlineData("utf8")] [InlineData("duplicates")] [InlineData("sse")] [InlineData("charset")]
    public async Task OriginalResponseBoundariesAreEnforced(string kind)
    {
        var bytes = Encoding.UTF8.GetBytes(AzureEmbeddingFixture.Part("float_response").GetRawText()); if (kind == "utf8") bytes = [255];
        if (kind == "duplicates") bytes = Encoding.UTF8.GetBytes("{\"data\":[],\"future\":1,\"future\":2}");
        using var source = new AzureEmbeddingReadStream(bytes); using var factory = AzureEmbeddingFixture.Factory(); factory.OnSend = (_, _) =>
        { var content = new StreamContent(source); content.Headers.ContentType = new(kind == "sse" ? "text/event-stream" : "application/json"); if (kind == "charset") content.Headers.ContentType.CharSet = "utf-16"; if (kind == "length") content.Headers.ContentLength = bytes.Length + 1; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }); };
        await Assert.ThrowsAsync<InvalidDataException>(() => new AzureEmbeddingAdapter(factory, AzureEmbeddingFixture.Options(responseLimit: kind == "bytes" ? bytes.Length - 1 : bytes.Length + 100)).EmbedAsync(AzureEmbeddingFixture.Request(), TestContext.Current.CancellationToken));
        Assert.True(source.Disposed); Assert.Equal(1, factory.Disposals); Assert.Equal(1, factory.Calls);
    }
    /// <summary>最终读取、流/客户端释放与取消检查完成前不能公开成功向量。</summary>
    [Theory]
    [InlineData("read")] [InlineData("dispose")] [InlineData("client")] [InlineData("cancel")]
    public async Task LateFailureCannotReleaseSuccessfulVectors(string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var source = new AzureEmbeddingReadStream(Encoding.UTF8.GetBytes(AzureEmbeddingFixture.Part("float_response").GetRawText())) { FailRead = kind == "read", FailDispose = kind == "dispose" };
        using var factory = AzureEmbeddingFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) });
        if (kind == "client") factory.OnDisposeClient = () => throw new IOException("fixture client disposal"); if (kind == "cancel") factory.OnDisposeClient = cancellation.Cancel;
        if (kind == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AzureEmbeddingAdapter(factory, AzureEmbeddingFixture.Options()).EmbedAsync(AzureEmbeddingFixture.Request(), cancellation.Token));
        else await Assert.ThrowsAsync<IOException>(() => new AzureEmbeddingAdapter(factory, AzureEmbeddingFixture.Options()).EmbedAsync(AzureEmbeddingFixture.Request(), cancellation.Token));
        Assert.True(source.Disposed); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }
}
