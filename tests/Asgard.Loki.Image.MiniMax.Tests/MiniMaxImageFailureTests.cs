using System.Net;
using System.Text;
using Xunit;

namespace Asgard.Loki.Image.MiniMax.Tests;

/// <summary>MiniMax 业务错误、协议边界及 HTTP 所有权的风险回归。</summary>
public sealed class MiniMaxImageFailureTests
{
    /// <summary>HTTP 与成功状态中的非零 base_resp 均保留完整原文且不重试。</summary>
    [Theory]
    [InlineData(200)] [InlineData(429)]
    public async Task OriginalNativeErrorIsNotMasked(int status)
    {
        var data = MiniMaxFixture.Part("error"); using var factory = new MiniMaxTestFactory { OnSend = (_, _) => Task.FromResult(MiniMaxFixture.Json(data, (HttpStatusCode)status)) };
        var error = await Assert.ThrowsAsync<ImageUpstreamException>(() => new MiniMaxImageAdapter(factory, MiniMaxFixture.Options()).GenerateAsync(MiniMaxFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.Equal(data.GetRawText(), error.ResponseBody); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>缺失/损坏的原生状态或图像载荷不能成为成功结果。</summary>
    [Theory]
    [InlineData("{}")] [InlineData("{\"base_resp\":{\"status_code\":\"0\"}}")]
    [InlineData("{\"base_resp\":{\"status_code\":0},\"data\":null}")]
    [InlineData("{\"base_resp\":{\"status_code\":0},\"data\":{\"image_urls\":[null]}}")]
    [InlineData("{\"base_resp\":{\"status_code\":0},\"data\":{\"image_base64\":false}}")]
    public async Task InvalidNativeEnvelopeCannotSucceed(string json)
    {
        using var factory = new MiniMaxTestFactory { OnSend = (_, _) => Task.FromResult(MiniMaxFixture.Json(MiniMaxFixture.Element(json))) };
        await Assert.ThrowsAsync<InvalidDataException>(() => new MiniMaxImageAdapter(factory, MiniMaxFixture.Options()).GenerateAsync(MiniMaxFixture.Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>严格字节、长度和重复属性检查覆盖原生完整响应。</summary>
    [Theory]
    [InlineData("utf8")] [InlineData("duplicate")] [InlineData("length-short")] [InlineData("length-long")] [InlineData("limit")]
    public async Task InvalidBodyOrLengthCannotSucceed(string fault)
    {
        var bytes = Encoding.UTF8.GetBytes(MiniMaxFixture.Part("url").GetRawText());
        if (fault == "utf8") bytes = [.. bytes, 0xff];
        if (fault == "duplicate") bytes = Encoding.UTF8.GetBytes("{\"base_resp\":{\"status_code\":0},\"base_resp\":{\"status_code\":1},\"data\":{}}");
        using var factory = new MiniMaxTestFactory { OnSend = (_, _) =>
        { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; response.Content.Headers.ContentType = new("application/json"); if (fault.StartsWith("length-", StringComparison.Ordinal)) response.Content.Headers.ContentLength = bytes.Length + (fault == "length-short" ? -1 : 1); return Task.FromResult(response); } };
        await Assert.ThrowsAsync<InvalidDataException>(() => new MiniMaxImageAdapter(factory, MiniMaxFixture.Options(responseLimit: fault == "limit" ? 1 : 65536)).GenerateAsync(MiniMaxFixture.Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>保留模型/prompt 绑定并在网络前拒绝流式模式和超限。</summary>
    [Theory]
    [InlineData("MODEL")] [InlineData("prompt")] [InlineData("stream")] [InlineData("limit")]
    public void InvalidRequestFailsBeforeNetwork(string fault)
    {
        using var factory = new MiniMaxTestFactory(); var request = MiniMaxFixture.Request();
        if (fault != "limit") request.Parameters[fault] = MiniMaxFixture.Element(fault == "stream" ? "true" : "\"override\"");
        var adapter = new MiniMaxImageAdapter(factory, MiniMaxFixture.Options(requestLimit: fault == "limit" ? 1 : 32768));
        if (fault == "stream") Assert.Throws<NotSupportedException>(() => { _ = adapter.GenerateAsync(request, TestContext.Current.CancellationToken); });
        else Assert.Throws<ArgumentException>(() => { _ = adapter.GenerateAsync(request, TestContext.Current.CancellationToken); }); Assert.Equal(0, factory.Creates);
    }
    /// <summary>原生不支持的 SSE 在网络前明确拒绝，不产生合成事件。</summary>
    [Fact]
    public void StreamIsExplicitlyUnsupported()
    {
        using var factory = new MiniMaxTestFactory(); Assert.Throws<NotSupportedException>(() => new MiniMaxImageAdapter(factory, MiniMaxFixture.Options()).StreamAsync(MiniMaxFixture.Request(), TestContext.Current.CancellationToken)); Assert.Equal(0, factory.Creates);
    }
    /// <summary>取消和晚释放失败不会返回成功，也不重复生成。</summary>
    [Theory]
    [InlineData("cancel-before")] [InlineData("cancel-factory")] [InlineData("dispose")]
    public async Task OwnershipAndCancellationAreObserved(string fault)
    {
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); using var factory = new MiniMaxTestFactory();
        if (fault == "cancel-before") canceled.Cancel(); if (fault == "cancel-factory") factory.OnCreate = canceled.Cancel;
        if (fault == "dispose") factory.OnDisposeClient = () => throw new IOException("fixture disposal");
        if (fault == "dispose") await Assert.ThrowsAsync<IOException>(() => new MiniMaxImageAdapter(factory, MiniMaxFixture.Options()).GenerateAsync(MiniMaxFixture.Request(), canceled.Token));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MiniMaxImageAdapter(factory, MiniMaxFixture.Options()).GenerateAsync(MiniMaxFixture.Request(), canceled.Token));
        Assert.Equal(fault == "dispose" ? 1 : 0, factory.Calls); Assert.False(factory.Disposed);
    }
}
