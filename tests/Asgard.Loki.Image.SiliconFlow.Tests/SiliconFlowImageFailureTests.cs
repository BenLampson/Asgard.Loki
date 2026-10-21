using System.Net;
using System.Text;
using Xunit;

namespace Asgard.Loki.Image.SiliconFlow.Tests;

/// <summary>SiliconFlow 业务错误、协议边界及 HTTP 所有权的风险回归。</summary>
public sealed class SiliconFlowImageFailureTests
{
    /// <summary>HTTP 与成功状态中的非零原生 code 均保留完整原文且不重试。</summary>
    [Theory]
    [InlineData(200)] [InlineData(429)]
    public async Task OriginalNativeErrorIsNotMasked(int status)
    {
        var data = SiliconFlowFixture.Part("error"); using var factory = new SiliconFlowTestFactory { OnSend = (_, _) => Task.FromResult(SiliconFlowFixture.Json(data, (HttpStatusCode)status)) };
        var error = await Assert.ThrowsAsync<ImageUpstreamException>(() => new SiliconFlowImageAdapter(factory, SiliconFlowFixture.Options()).GenerateAsync(SiliconFlowFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.Equal(data.GetRawText(), error.ResponseBody); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>原生纯文本认证错误或仅 message 的错误同样保留完整正文。</summary>
    [Theory]
    [InlineData(401, "Invalid token", "text/plain")]
    [InlineData(200, "{\"message\":\"fixture rate limit\",\"data\":\"trace\"}", "application/json")]
    public async Task NativeAlternateErrorBodiesRemainOriginal(int status, string body, string type)
    {
        using var factory = new SiliconFlowTestFactory { OnSend = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, type) }) };
        var error = await Assert.ThrowsAsync<ImageUpstreamException>(() => new SiliconFlowImageAdapter(factory, SiliconFlowFixture.Options()).GenerateAsync(SiliconFlowFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.Equal(body, error.ResponseBody); Assert.Equal(1, factory.Calls);
    }
    /// <summary>缺失/损坏的原生图像封装或载荷不能成为成功结果。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"images\":null}")]
    [InlineData("{\"images\":[null]}")]
    [InlineData("{\"images\":[{}]}")]
    [InlineData("{\"images\":[{\"url\":false}]}")]
    public async Task InvalidNativeEnvelopeCannotSucceed(string json)
    {
        using var factory = new SiliconFlowTestFactory { OnSend = (_, _) => Task.FromResult(SiliconFlowFixture.Json(SiliconFlowFixture.Element(json))) };
        await Assert.ThrowsAsync<InvalidDataException>(() => new SiliconFlowImageAdapter(factory, SiliconFlowFixture.Options()).GenerateAsync(SiliconFlowFixture.Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>严格字节、长度和重复属性检查覆盖原生完整响应。</summary>
    [Theory]
    [InlineData("utf8")] [InlineData("duplicate")] [InlineData("length-short")] [InlineData("length-long")] [InlineData("limit")]
    public async Task InvalidBodyOrLengthCannotSucceed(string fault)
    {
        var bytes = Encoding.UTF8.GetBytes(SiliconFlowFixture.Part("response").GetRawText());
        if (fault == "utf8") bytes = [.. bytes, 0xff];
        if (fault == "duplicate") bytes = Encoding.UTF8.GetBytes("{\"images\":[],\"images\":[{\"url\":\"https://example.invalid\"}]}");
        using var factory = new SiliconFlowTestFactory { OnSend = (_, _) =>
        { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; response.Content.Headers.ContentType = new("application/json"); if (fault.StartsWith("length-", StringComparison.Ordinal)) response.Content.Headers.ContentLength = bytes.Length + (fault == "length-short" ? -1 : 1); return Task.FromResult(response); } };
        await Assert.ThrowsAsync<InvalidDataException>(() => new SiliconFlowImageAdapter(factory, SiliconFlowFixture.Options(responseLimit: fault == "limit" ? 1 : 65536)).GenerateAsync(SiliconFlowFixture.Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>保留模型/prompt 绑定并在网络前拒绝流式模式和超限。</summary>
    [Theory]
    [InlineData("MODEL")] [InlineData("prompt")] [InlineData("stream")] [InlineData("limit")]
    public void InvalidRequestFailsBeforeNetwork(string fault)
    {
        using var factory = new SiliconFlowTestFactory(); var request = SiliconFlowFixture.Request();
        if (fault != "limit") request.Parameters[fault] = SiliconFlowFixture.Element(fault == "stream" ? "true" : "\"override\"");
        var adapter = new SiliconFlowImageAdapter(factory, SiliconFlowFixture.Options(requestLimit: fault == "limit" ? 1 : 32768));
        if (fault == "stream") Assert.Throws<NotSupportedException>(() => { _ = adapter.GenerateAsync(request, TestContext.Current.CancellationToken); });
        else Assert.Throws<ArgumentException>(() => { _ = adapter.GenerateAsync(request, TestContext.Current.CancellationToken); }); Assert.Equal(0, factory.Creates);
    }
    /// <summary>原生不支持的 SSE 在网络前明确拒绝，不产生合成事件。</summary>
    [Fact]
    public void StreamIsExplicitlyUnsupported()
    {
        using var factory = new SiliconFlowTestFactory(); Assert.Throws<NotSupportedException>(() => new SiliconFlowImageAdapter(factory, SiliconFlowFixture.Options()).StreamAsync(SiliconFlowFixture.Request(), TestContext.Current.CancellationToken)); Assert.Equal(0, factory.Creates);
    }
    /// <summary>取消和晚释放失败不会返回成功，也不重复生成。</summary>
    [Theory]
    [InlineData("cancel-before")] [InlineData("cancel-factory")] [InlineData("dispose")]
    public async Task OwnershipAndCancellationAreObserved(string fault)
    {
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); using var factory = new SiliconFlowTestFactory();
        if (fault == "cancel-before") canceled.Cancel(); if (fault == "cancel-factory") factory.OnCreate = canceled.Cancel;
        if (fault == "dispose") factory.OnDisposeClient = () => throw new IOException("fixture disposal");
        if (fault == "dispose") await Assert.ThrowsAsync<IOException>(() => new SiliconFlowImageAdapter(factory, SiliconFlowFixture.Options()).GenerateAsync(SiliconFlowFixture.Request(), canceled.Token));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SiliconFlowImageAdapter(factory, SiliconFlowFixture.Options()).GenerateAsync(SiliconFlowFixture.Request(), canceled.Token));
        Assert.Equal(fault == "dispose" ? 1 : 0, factory.Calls); Assert.False(factory.Disposed);
    }
}
