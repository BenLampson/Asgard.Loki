using System.Net;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Anthropic.Tests.AnthropicHttpFixture;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>原生终态、异步/同步释放与最终取消的实际 HTTP 故障回归。</summary>
public sealed class NativeMessagesTerminalTests
{
    /// <summary>两种认证和 JSON/SSE 都必须传播原生结束后的读取/释放/取消故障。</summary>
    public static IEnumerable<object[]> Faults()
    {
        foreach (var bearer in new[] { false, true }) foreach (var stream in new[] { false, true })
            foreach (var fault in new[] { "read", "body", "client", "eof-cancel", "body-cancel", "client-cancel" }) yield return [bearer, stream, fault];
    }
    /// <summary>任何晚期故障都不能让暂存的结束原因或消息完成逃逸。</summary>
    [Theory] [MemberData(nameof(Faults))]
    public async Task LateFailure_WithholdsSuccess(bool bearer, bool stream, string fault)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var body = new OllamaBodyStream(Bytes(stream ? Finished : "{\"id\":\"msg\",\"content\":[],\"future\":false}"))
        { FailAtEof = fault == "read", FailDispose = fault == "body", OnEof = fault == "eof-cancel" ? cancel.Cancel : null, OnDispose = fault == "body-cancel" ? cancel.Cancel : null };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, stream)));
        var factory = new AnthropicHttpFactory(handler) { OnDispose = () => { if (fault == "client") throw new IOException("client dispose"); if (fault == "client-cancel") cancel.Cancel(); } };
        var provider = new AnthropicLlmProvider(factory, new AnthropicHttpOptions { Authentication = bearer ? AnthropicAuthentication.Bearer : AnthropicAuthentication.ApiKey }); List<ClaudeMessageEvent> seen = [];
        var error = await Record.ExceptionAsync(async () => { if (stream) await Drain(provider, cancel.Token, seen); else await provider.CreateAsync(new JsonObject(), Context(provider), cancel.Token); });
        if (fault.EndsWith("cancel", StringComparison.Ordinal)) Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.IsType<IOException>(error);
        Assert.DoesNotContain(seen, item => item.Type is "message_delta" or "message_stop"); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生结束原因、stop 与未知尾帧均在正常 EOF 及释放后交付，首个开始帧仍即时返回。</summary>
    [Fact]
    public async Task TerminalAndUnknownTail_WaitForEofAndDisposal()
    {
        var body = new OllamaBodyStream(Bytes(Finished + Frame("{\"type\":\"vendor.future\",\"error\":{\"business\":true}}")));
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new AnthropicHttpFactory(handler); List<ClaudeMessageEvent> seen = [];
        await Drain(new(factory), TestContext.Current.CancellationToken, seen, item =>
        {
            if (item.Type == "message_start") { Assert.False(body.Eof); Assert.False(body.Disposed); }
            else { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); }
        }); Assert.Equal(4, seen.Count); Assert.Equal("vendor.future", seen[^1].Type);
    }
    /// <summary>迟到错误保留原始 HTTP 状态和正文，不能被允许损耗或终态覆盖。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeError_AfterStopOrReason_DiscardsSuccess(bool afterStop)
    {
        const string error = "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"future\":false}}";
        var wire = Frame(Start) + Frame(Finish) + (afterStop ? Frame(Stop) : "") + Frame(error);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(wire)), true))); List<ClaudeMessageEvent> seen = [];
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Drain(new(new AnthropicHttpFactory(handler)), TestContext.Current.CancellationToken, seen));
        Assert.Equal(HttpStatusCode.OK, failure.StatusCode); Assert.Equal(error, failure.ResponseBody); Assert.DoesNotContain(seen, item => item.Type is "message_delta" or "message_stop"); Assert.Equal(1, handler.Calls);
    }
    /// <summary>提前结束枚举只释放本次资源，不读取或合成剩余终态。</summary>
    [Fact]
    public async Task EarlyExit_RemainsIncrementalAndReleases()
    {
        var body = new OllamaBodyStream(Bytes(Finished)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new AnthropicHttpFactory(handler); var provider = new AnthropicLlmProvider(factory);
        await foreach (var item in provider.StreamAsync(new(), Context(provider), TestContext.Current.CancellationToken)) { Assert.Equal("message_start", item.Type); Assert.False(body.Eof); break; }
        Assert.False(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
