using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>有界原始传输、编码/长度、真实目标和释放后终态，不访问模型或媒体。</summary>
public sealed class AzureTextCompletionTransportTests
{
    /// <summary>二进制错误及不可解析媒体类型保持原字节，不做有损替换或重试。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task BinaryErrorsRetainBytesAndRawType(bool malformed)
    {
        byte[] bytes = [255, 0, 254]; using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) =>
        { var content = new ByteArrayContent(bytes); content.Headers.TryAddWithoutValidation("Content-Type", malformed ? "not a media type" : "application/octet-stream"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = content }); };
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => AzureTextFixture.Invoke(new AzureLlmProvider(factory), TestContext.Current.CancellationToken));
        Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal(malformed ? "not a media type" : "application/octet-stream", error.ContentType); Assert.Throws<DecoderFallbackException>(() => error.ResponseBody); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>未知成员也必须是严格 UTF-8/Unicode，媒体/长度/字节上限错误不发布成功。</summary>
    [Theory] [InlineData("limit")] [InlineData("length")] [InlineData("utf8")] [InlineData("unicode")] [InlineData("charset")] [InlineData("type")] [InlineData("invalid-type")]
    public async Task FullNativeBodyMustMeetTransportContract(string kind)
    {
        var bytes = Encoding.UTF8.GetBytes(AzureTextFixture.Part("response").GetRawText());
        const string prefix = "{\"id\":\"cmpl\",\"object\":\"text_completion\",\"model\":\"native\",\"created\":0,\"choices\":[{\"index\":0,\"text\":\"\",\"finish_reason\":\"stop\"}],\"future\":\"";
        if (kind == "utf8") bytes = Encoding.UTF8.GetBytes(prefix).Concat(new byte[] { 255 }).Concat(Encoding.UTF8.GetBytes("\"}")).ToArray();
        if (kind == "unicode") bytes = Encoding.UTF8.GetBytes(prefix + "\\ud800\"}");
        using var stream = new AzureCompactionReadStream(bytes); using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) =>
        {
            var content = new StreamContent(stream); content.Headers.ContentType = new(kind == "type" ? "text/plain" : "application/json");
            if (kind == "charset") content.Headers.ContentType.CharSet = "utf-16"; if (kind == "length") content.Headers.ContentLength = bytes.Length + 1;
            if (kind == "invalid-type") { content.Headers.Remove("Content-Type"); content.Headers.TryAddWithoutValidation("Content-Type", "not a media type"); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        };
        var provider = new AzureLlmProvider(factory, new() { MaximumBodyBytes = kind == "limit" ? bytes.Length - 1 : bytes.Length + 100 });
        await Assert.ThrowsAsync<InvalidDataException>(() => AzureTextFixture.Invoke(provider, TestContext.Current.CancellationToken)); Assert.True(stream.Disposed); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>重复未知成员与截断/非 JSON 同样不能发布已读前缀。</summary>
    [Theory] [InlineData("not-json")] [InlineData("{\"output\":[")]
    [InlineData("{\"id\":\"cmpl\",\"object\":\"text_completion\",\"model\":\"native\",\"created\":0,\"choices\":[{\"index\":0,\"text\":\"\",\"finish_reason\":\"stop\"}],\"future\":{\"same\":1,\"same\":2}}")]
    public async Task InvalidOrDuplicateJsonCannotBecomeSuccess(string raw)
    {
        using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw, Encoding.UTF8, "application/json") });
        await Assert.ThrowsAnyAsync<JsonException>(() => AzureTextFixture.Invoke(new AzureLlmProvider(factory), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>精确响应字节上限允许成功，完整原生 JSON 和资源释放仍保持。</summary>
    [Fact]
    public async Task ExactByteLimitRetainsCompleteJson()
    {
        var expected = AzureTextFixture.Part("response"); var bytes = Encoding.UTF8.GetBytes(expected.GetRawText()); using var stream = new AzureCompactionReadStream(bytes); using var factory = AzureTextFixture.Factory();
        factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        var result = await AzureTextFixture.Invoke(new AzureLlmProvider(factory, new() { MaximumBodyBytes = bytes.Length }), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(expected, result.Data)); Assert.True(stream.Eof); Assert.True(stream.Disposed); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>读完数据后的读取、流/客户端释放或最终取消失败不能提前交付结果。</summary>
    [Theory] [InlineData("read")] [InlineData("dispose")] [InlineData("client")] [InlineData("cancel")]
    public async Task LateFailuresPreventSuccessfulPublication(string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var stream = new AzureCompactionReadStream(Encoding.UTF8.GetBytes(AzureTextFixture.Part("response").GetRawText())) { FailRead = kind == "read", FailDispose = kind == "dispose" };
        using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        if (kind == "client") factory.OnDisposeClient = () => throw new IOException("fixture client disposal"); if (kind == "cancel") factory.OnDisposeClient = cancellation.Cancel;
        if (kind == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AzureTextFixture.Invoke(new AzureLlmProvider(factory), cancellation.Token));
        else await Assert.ThrowsAsync<IOException>(() => AzureTextFixture.Invoke(new AzureLlmProvider(factory), cancellation.Token));
        Assert.True(stream.Disposed); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed); Assert.Equal(1, factory.Calls);
    }
    /// <summary>SSE 在正文读取前失败，不等待没有终止事件的流。</summary>
    [Fact]
    public async Task SseIsRefusedBeforeRead()
    {
        using var stream = new AzureCompactionReadStream(Encoding.UTF8.GetBytes("data: never ending\n\n")); using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) =>
        { var content = new StreamContent(stream); content.Headers.ContentType = new("text/event-stream"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }); };
        await Assert.ThrowsAsync<InvalidDataException>(() => AzureTextFixture.Invoke(new AzureLlmProvider(factory), TestContext.Current.CancellationToken)); Assert.False(stream.Eof); Assert.True(stream.Disposed);
    }
    /// <summary>实际响应地址或方法改变不能当作原目标的结果。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ChangedActualTargetCannotBePublished(bool method)
    {
        using var actual = new HttpRequestMessage(method ? HttpMethod.Put : HttpMethod.Post, method ? "https://azure-text.example.invalid/proxy/openai/v1/completions" : "https://other.example.invalid/completions");
        using var stream = new AzureCompactionReadStream(Encoding.UTF8.GetBytes(AzureTextFixture.Part("response").GetRawText())); using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream), RequestMessage = actual });
        await Assert.ThrowsAsync<InvalidDataException>(() => AzureTextFixture.Invoke(new AzureLlmProvider(factory), TestContext.Current.CancellationToken)); Assert.False(stream.Eof); Assert.True(stream.Disposed); Assert.Equal(1, factory.Disposals);
    }
}
