using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>原生文本补全 SSE 的编码、认证期限和提前消费退出，有限终态不会提前发布。</summary>
public sealed class AzureTextStreamTransportTests
{
    /// <summary>流式响应的身份变化/到期发生在收头、EOF、客户端释放或下一帧时均拒绝交付。</summary>
    [Theory] [InlineData("source", "headers")] [InlineData("expiry", "headers")]
    [InlineData("source", "eof")] [InlineData("expiry", "eof")] [InlineData("source", "dispose")] [InlineData("expiry", "dispose")]
    [InlineData("source", "frame")] [InlineData("expiry", "frame")]
    public async Task StreamAuthenticationMustRemainValidForDelivery(string kind, string boundary)
    {
        var clock = new AzureClock(); using var source = new AzureTokenSource(clock); using var factory = AzureTextFixture.Factory();
        void Change() { if (kind == "source") source.Version = "changed"; else clock.Now = clock.Now.AddHours(1); }
        using var body = new AzureCompactionReadStream(Encoding.UTF8.GetBytes(AzureTextFixture.Sse())) { OnEof = boundary == "eof" ? Change : null }; factory.OnSend = (_, _) => { if (boundary == "headers") Change(); return Task.FromResult(AzureTextFixture.Streaming(body)); }; if (boundary == "dispose") factory.OnDisposeClient = Change;
        var provider = new AzureLlmProvider(factory, tokenSource: source, timeProvider: clock); var seen = new List<TextCompletionEvent>();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await foreach (var item in ((ITextCompletionsAdapter)provider).StreamAsync(AzureTextFixture.Payload(true), AzureTextFixture.Context(true), TestContext.Current.CancellationToken)) { seen.Add(item); if (boundary == "frame") Change(); } });
        Assert.Equal(boundary == "headers" ? 0 : boundary == "frame" ? 1 : 3, seen.Count); Assert.True(body.Disposed); Assert.Equal(1, source.Calls); Assert.False(source.Disposed); Assert.Equal(1, factory.Disposals); Assert.Equal(1, factory.Calls); if (boundary == "headers") Assert.False(body.Eof);
    }
    /// <summary>原始编码、转义与重复未知属性不能通过 SSE 分帧变成有效原生事件。</summary>
    [Theory] [InlineData("utf8")] [InlineData("unicode")] [InlineData("duplicate")] [InlineData("array")] [InlineData("charset")]
    public async Task StrictNativeSseEncodingIsRequired(string kind)
    {
        var raw = kind switch { "unicode" => "data: {\"future\":\"\\ud800\"}\n\n", "duplicate" => "data: {\"future\":{\"same\":1,\"same\":2}}\n\n", "array" => "data: []\n\n", _ => AzureTextFixture.Sse() }; var bytes = kind == "utf8" ? Encoding.UTF8.GetBytes("data: {\"future\":\"").Concat(new byte[] { 255 }).Concat(Encoding.UTF8.GetBytes("\"}\n\n")).ToArray() : Encoding.UTF8.GetBytes(raw);
        using var body = new AzureCompactionReadStream(bytes); using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) => { var reply = AzureTextFixture.Streaming(body); if (kind == "charset") reply.Content.Headers.ContentType!.CharSet = "utf-16"; return Task.FromResult(reply); };
        var error = await Record.ExceptionAsync(() => AzureTextFixture.Drain(((ITextCompletionsAdapter)new AzureLlmProvider(factory)).StreamAsync(AzureTextFixture.Payload(true), AzureTextFixture.Context(), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)); Assert.NotNull(error);
        if (kind == "utf8") Assert.IsType<DecoderFallbackException>(error); else if (kind == "duplicate") Assert.IsAssignableFrom<JsonException>(error); else Assert.IsType<InvalidDataException>(error); Assert.True(body.Disposed); Assert.Equal(1, factory.Disposals); if (kind == "charset") Assert.False(body.Eof);
    }
    /// <summary>调用者提前停止消费立即释放本次资源，不自动读完、补造结束或发起第二次请求。</summary>
    [Fact]
    public async Task EarlyConsumerExitDoesNotDrainOrRetry()
    {
        using var body = new AzureCompactionReadStream(Encoding.UTF8.GetBytes(AzureTextFixture.Sse())); using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(AzureTextFixture.Streaming(body)); await using var client = AzureTextFixture.Build(new AzureLlmProvider(factory)); var count = 0;
        await foreach (var _ in client.LLM.TextCompletions.StreamAsync(AzureTextFixture.Request(true), AzureTextFixture.Call(), TestContext.Current.CancellationToken)) { count++; break; }
        Assert.Equal(1, count); Assert.True(body.Disposed); Assert.False(body.Eof); Assert.Equal(1, factory.Disposals); Assert.Equal(1, factory.Calls); Assert.False(factory.Disposed);
    }
    /// <summary>流式调用的非 SSE HTTP 错误仍完整保留二进制正文及媒体类型，不自动改成完整生成。</summary>
    [Fact]
    public async Task BinaryHttpErrorRetainsOriginalStreamFailure()
    {
        byte[] bytes = [255, 0, 254]; using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) => { var content = new ByteArrayContent(bytes); content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream"); return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway) { Content = content }); };
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => AzureTextFixture.Drain(((ITextCompletionsAdapter)new AzureLlmProvider(factory)).StreamAsync(AzureTextFixture.Payload(true), AzureTextFixture.Context(), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)); Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal("application/octet-stream", error.ContentType); Assert.Throws<DecoderFallbackException>(() => error.ResponseBody); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
}
