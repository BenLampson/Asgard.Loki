using System.Net;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>实际网络尾部、释放和取消必须先于可能成功的原生终态。</summary>
public sealed class HttpTerminalTests
{
    /// <summary>默认 message 事件与省略 event 等价；首个原生错误不因缺少 DONE 被误报截断。</summary>
    [Theory]
    [InlineData(false, "")] [InlineData(true, "")]
    [InlineData(false, "event: message\n")] [InlineData(true, "event: message\n")]
    public async Task NativeRootError_RemainsAnErrorEvent(bool responses, string eventPrefix)
    {
        const string error = "{\"error\":{\"code\":\"native\",\"future\":false}}";
        var body = new OllamaBodyStream(Bytes(eventPrefix + Frame(error)));
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler); List<LlmJsonEvent> items = [];
        await Drain(new(factory), responses, TestContext.Current.CancellationToken, items, _ => { Assert.True(body.Eof); Assert.True(body.Disposed); });
        Assert.Equal(error, Assert.Single(items).Data.GetRawText()); Assert.Equal(1, handler.Calls);
    }

    /// <summary>正常 EOF、响应及客户端释放先于终态交付；未知尾帧仍保留。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Terminal_WaitsForEofAndDisposal(bool responses)
    {
        var tail = Frame("{\"type\":\"vendor.future\",\"error\":{\"business\":true},\"response\":{\"id\":\"opaque\"},\"number\":9007199254740993}");
        var wire = Frame(responses ? ResponseFinish : ChatFinish) + tail + (responses ? "" : Frame("[DONE]"));
        var body = new OllamaBodyStream(Bytes(wire));
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true, Bytes(wire).Length)));
        var factory = new OpenAiHttpFactory(handler); var provider = new OpenAiLlmProvider(factory); List<LlmJsonEvent> items = [];
        await Drain(provider, responses, TestContext.Current.CancellationToken, items, _ =>
        { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); });
        Assert.Equal(2, items.Count); Assert.Equal("9007199254740993", items[1].Data.GetProperty("number").GetRawText()); Assert.Equal(1, handler.Calls);
    }

    /// <summary>迟到读取/释放/最终取消故障不得发布已暂存的成功终态。</summary>
    [Theory]
    [InlineData(false, "read")] [InlineData(true, "read")]
    [InlineData(false, "body")] [InlineData(true, "body")]
    [InlineData(false, "client")] [InlineData(true, "client")]
    [InlineData(false, "eof-cancel")] [InlineData(true, "eof-cancel")]
    [InlineData(false, "body-cancel")] [InlineData(true, "body-cancel")]
    [InlineData(false, "client-cancel")] [InlineData(true, "client-cancel")]
    public async Task LateFailure_DiscardsPendingSuccess(bool responses, string fault)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var body = new OllamaBodyStream(Bytes(Finished(responses)))
        { FailAtEof = fault == "read", FailDispose = fault == "body", OnEof = fault == "eof-cancel" ? cancellation.Cancel : null, OnDispose = fault == "body-cancel" ? cancellation.Cancel : null };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true)));
        var factory = new OpenAiHttpFactory(handler) { OnDispose = () => { if (fault == "client") throw new IOException("client dispose"); if (fault == "client-cancel") cancellation.Cancel(); } };
        List<LlmJsonEvent> items = [];
        var error = await Record.ExceptionAsync(() => Drain(new(factory), responses, cancellation.Token, items));
        if (fault.EndsWith("cancel", StringComparison.Ordinal)) Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.IsType<IOException>(error);
        Assert.Empty(items); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>DONE 后不能忽略追加事件、重复 DONE 或未闭合事件；注释允许但也必须读完。</summary>
    [Theory]
    [InlineData("data: [DONE]\n\n")]
    [InlineData("data: {}\n\n")]
    [InlineData("data: {\"error\":{\"code\":\"late\"}}\n\n")]
    [InlineData("data: {\"unfinished\":true}")]
    public async Task DoneTail_IsReadAndRejected(string tail)
    {
        var body = new OllamaBodyStream(Bytes(Finished(false) + tail));
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true)));
        List<LlmJsonEvent> items = []; var error = await Record.ExceptionAsync(() => Drain(new(new OpenAiHttpFactory(handler)), false, TestContext.Current.CancellationToken, items));
        Assert.NotNull(error); Assert.Empty(items); Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>首个增量即时交付，提前结束枚举释放资源，不读取或合成终态。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task EarlyBreak_PreservesIncrementalityAndReleases(bool responses)
    {
        var delta = responses ? "{\"type\":\"response.output_text.delta\",\"delta\":\"半段\"}" : "{\"id\":\"c\",\"choices\":[{\"index\":2,\"delta\":{\"content\":\"半段\"},\"finish_reason\":null}]}";
        var body = new OllamaBodyStream(Bytes(Frame(delta) + Finished(responses)));
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler); var provider = new OpenAiLlmProvider(factory);
        if (responses) { await foreach (var item in ((IResponsesAdapter)provider).StreamAsync(new(), Context(provider), TestContext.Current.CancellationToken)) { Assert.False(body.Eof); Assert.Equal(delta, item.Data.GetRawText()); break; } }
        else { await foreach (var item in provider.StreamAsync(new(), Context(provider), TestContext.Current.CancellationToken)) { Assert.False(body.Eof); Assert.Equal(delta, item.Data.GetRawText()); break; } }
        Assert.False(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }

    /// <summary>失败和不完整状态保持原生事件，不改成 completed。</summary>
    [Theory]
    [InlineData("failed")] [InlineData("incomplete")]
    public async Task NativeNonSuccess_RemainsNative(string status)
    {
        var json = $"{{\"type\":\"response.{status}\",\"response\":{{\"id\":\"r\",\"status\":\"{status}\",\"error\":{{\"code\":\"native\"}}}}}}";
        var body = new OllamaBodyStream(Bytes(Frame(json))); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); List<LlmJsonEvent> items = [];
        await Drain(new(new OpenAiHttpFactory(handler)), true, TestContext.Current.CancellationToken, items); Assert.Equal(json, Assert.Single(items).Data.GetRawText()); Assert.True(body.Eof);
    }

    /// <summary>成功候选之后的原生错误保留原始状态和事件正文，不能逃逸成功。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LateNativeError_PreservesOriginalFailure(bool responses)
    {
        const string error = "{\"type\":\"error\",\"code\":\"late\",\"future\":false}";
        var wire = Frame(responses ? ResponseFinish : ChatFinish) + Frame(error);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new(Bytes(wire)), true))); List<LlmJsonEvent> items = [];
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Drain(new(new OpenAiHttpFactory(handler)), responses, TestContext.Current.CancellationToken, items));
        Assert.Equal(HttpStatusCode.OK, failure.StatusCode); Assert.Equal(error, failure.ResponseBody); Assert.Empty(items); Assert.Equal(1, handler.Calls);
    }
}
