using System.Net;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>原生 Nova stop/用量尾部之后的读取、释放和取消故障，不能被二进制解码成功掩盖。</summary>
public sealed class NovaStreamTransportTests
{
    /// <summary>签名与显式 Key 两种认证共享同一正确终态边界。</summary>
    public static IEnumerable<object[]> Faults()
    {
        foreach (var apiKey in new[] { false, true }) foreach (var fault in new[] { "read", "body", "client", "eof-cancel", "body-cancel", "client-cancel" }) yield return [apiKey, fault];
    }
    /// <summary>完整源终态后仍必须观察实际 EOF、释放和最终取消。</summary>
    [Theory] [MemberData(nameof(Faults))]
    public async Task LateFault_WithholdsSuccessfulTerminal(bool apiKey, string fault)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var clock = new AwsClock(); var source = new AwsCredentialSource(clock);
        var body = new OllamaBodyStream(NovaStreamFixture.Complete()) { FailAtEof = fault == "read", FailDispose = fault == "body", OnEof = fault == "eof-cancel" ? cancel.Cancel : null, OnDispose = fault == "body-cancel" ? cancel.Cancel : null };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(body))); var factory = new AwsHttpFactory(handler) { OnDispose = () => { if (fault == "client") throw new IOException("client"); if (fault == "client-cancel") cancel.Cancel(); } };
        var provider = apiKey ? new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" }) : new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock); List<BedrockNovaEvent> seen = [];
        var error = await Record.ExceptionAsync(() => NovaStreamFixture.Drain(provider, cancel.Token, seen, apiKey: apiKey)); if (fault.EndsWith("cancel", StringComparison.Ordinal)) Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.IsType<IOException>(error);
        Assert.DoesNotContain(seen, item => item.Type is "messageStop" or "metadata"); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls); Assert.Equal(apiKey ? 0 : 1, source.Calls);
    }
    /// <summary>异步释放特有异常和取消必须先于任何原生成功终态。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AsyncOnlyDisposal_IsObserved(bool cancelled)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new MantleAsyncDisposeStream(NovaStreamFixture.Complete(), () => { if (cancelled) cancel.Cancel(); }, !cancelled); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(body))); List<BedrockNovaEvent> seen = [];
        var error = await Record.ExceptionAsync(() => NovaStreamFixture.Drain(new(new AwsHttpFactory(handler), new AwsLlmOptions { Region = "us-east-1" }), cancel.Token, seen, apiKey: true));
        if (cancelled) Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.IsType<IOException>(error); Assert.True(body.AsyncDisposed); Assert.DoesNotContain(seen, item => item.Type is "messageStop" or "metadata");
    }
    /// <summary>预先取消之外，调用者中止枚举也只释放本次资源，不读取或合成剩余完成。</summary>
    [Fact]
    public async Task EarlyExit_RemainsIncrementalAndReleases()
    {
        var body = new OllamaBodyStream(NovaStreamFixture.Complete()); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(body))); var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" });
        await foreach (var item in provider.NovaStreamAsync(NovaFixture.Request(), NovaFixture.Context(apiKey: true), TestContext.Current.CancellationToken)) { Assert.Equal("messageStart", item.Type); Assert.False(body.Eof); break; }
        Assert.False(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>HTTP 或未返回二进制的原生服务错误保留原始状态与正文。</summary>
    [Theory] [InlineData(429)] [InlineData(503)] [InlineData(200)]
    public async Task HttpErrors_KeepOriginalBody(int status)
    {
        const string json = "{\"error\":{\"code\":\"native\",\"future\":false}}"; using var handler = new StubHttpHandler((_, _) => Task.FromResult(MantleFixture.Reply(new MemoryStream(MantleFixture.Bytes(json)), false, status: (HttpStatusCode)status))); List<BedrockNovaEvent> seen = [];
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => NovaStreamFixture.Drain(new(new AwsHttpFactory(handler), new AwsLlmOptions { Region = "us-east-1" }), TestContext.Current.CancellationToken, seen, apiKey: true)); Assert.Equal((HttpStatusCode)status, failure.StatusCode); Assert.Equal(json, failure.ResponseBody); Assert.Null(failure.GetAwsEventStreamFrame()); Assert.Empty(seen); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原始总长度及字节限制在 source EOF 时校验，不能提前放出 stop。</summary>
    [Theory] [InlineData("exact")] [InlineData("short")] [InlineData("long")] [InlineData("limit")]
    public async Task RawStreamBounds_AreExact(string scenario)
    {
        var bytes = NovaStreamFixture.Complete(); using var handler = new StubHttpHandler((_, _) => { var reply = StreamFixture.Reply(bytes); reply.Content.Headers.ContentLength = bytes.Length + (scenario == "short" ? -1 : scenario == "long" ? 1 : 0); return Task.FromResult(reply); }); List<BedrockNovaEvent> seen = [];
        var provider = new AwsLlmProvider(new AwsHttpFactory(handler), new AwsLlmOptions { Region = "us-east-1", MaximumBodyBytes = bytes.Length - (scenario == "limit" ? 1 : 0) });
        if (scenario == "exact") await NovaStreamFixture.Drain(provider, TestContext.Current.CancellationToken, seen, apiKey: true);
        else { await Assert.ThrowsAsync<InvalidDataException>(() => NovaStreamFixture.Drain(provider, TestContext.Current.CancellationToken, seen, apiKey: true)); Assert.DoesNotContain(seen, item => item.Type is "messageStop" or "metadata"); }
        Assert.Equal(1, handler.Calls);
    }
}
