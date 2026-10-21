using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Volcengine.Tests.ArkFixture;

namespace Asgard.Loki.LLM.Volcengine.Tests;

/// <summary>每种原生协议与传输模式都必须在完整 EOF、长度和拥有资源关闭后交付成功。</summary>
public sealed class ArkLifecycleTests
{
    public static TheoryData<bool, bool, string> FaultCases
    {
        get
        {
            TheoryData<bool, bool, string> data = [];
            foreach (var responses in new[] { false, true }) foreach (var stream in new[] { false, true })
                foreach (var fault in new[] { "read", "dispose", "cancel-eof", "cancel-body", "cancel-client", "client", "late-uri", "length" }) data.Add(responses, stream, fault);
            return data;
        }
    }
    /// <summary>读取完仍可能取消、释放失败、声明长度不符或实际目标变化，成功/结束不得先逃逸。</summary>
    [Theory] [MemberData(nameof(FaultCases))]
    public async Task FinalBoundaryFailure_DoesNotPublishResultOrTerminal(bool responses, bool stream, string fault)
    {
        using var cancellation = new CancellationTokenSource(); var text = stream ? Text(responses ? "responses.sse" : "chat.sse") : Part(responses ? "responses_result" : "chat_result").GetRawText(); var bytes = Bytes(text);
        var body = new OllamaBodyStream(bytes) { FailAtEof = fault == "read", FailDispose = fault == "dispose", OnEof = fault == "cancel-eof" ? cancellation.Cancel : null, OnDispose = fault == "cancel-body" ? cancellation.Cancel : null };
        HttpRequestMessage? actual = null; using var handler = new StubHttpHandler((message, _) => { actual = message; return Task.FromResult(Reply(body, stream, fault == "length" ? bytes.Length + 1 : null)); });
        using var factory = new ArkHttpFactory(handler) { OnDispose = () => { if (fault == "cancel-client") cancellation.Cancel(); if (fault == "client") throw new IOException("client close"); if (fault == "late-uri") actual!.RequestUri = new("https://other.invalid/api/v3/"); } }; var provider = new VolcengineLlmProvider(factory); List<LlmJsonEvent> delivered = [];
        await Assert.ThrowsAnyAsync<Exception>(async () => { if (stream) await Stream(provider, responses, cancellation.Token, delivered.Add); else await Complete(provider, responses, cancellation.Token); });
        Assert.DoesNotContain(delivered, x => Ending(x.Data)); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>异步释放异常不能被同步释放掩盖，四种模式的原生结束都要等待该边界。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task AsyncCloseFailure_IsObservedBeforeSuccess(bool responses, bool stream)
    {
        var bytes = Bytes(stream ? Text(responses ? "responses.sse" : "chat.sse") : Part(responses ? "responses_result" : "chat_result").GetRawText()); var body = new ArkAsyncDisposeStream(bytes);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, stream))); using var factory = new ArkHttpFactory(handler); var provider = new VolcengineLlmProvider(factory); List<LlmJsonEvent> delivered = [];
        await Assert.ThrowsAsync<IOException>(async () => { if (stream) await Stream(provider, responses, TestContext.Current.CancellationToken, delivered.Add); else await Complete(provider, responses, TestContext.Current.CancellationToken); });
        Assert.True(body.AsyncDisposed); Assert.DoesNotContain(delivered, x => Ending(x.Data)); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>真实尾部错误包括 Chat DONE 之后的错误仍保留原始字节，不提前交付结束帧。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LateNativeError_AfterDoneOrTerminal_IsPreserved(bool responses)
    {
        const string error = "{\"type\":\"error\",\"error\":{\"message\":\"原生尾部失败\",\"future\":9007199254740993}}";
        var wire = Text(responses ? "responses.sse" : "chat.sse") + "data: " + error + Environment.NewLine + Environment.NewLine; var body = new OllamaBodyStream(Bytes(wire));
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); using var factory = new ArkHttpFactory(handler); List<LlmJsonEvent> delivered = [];
        var exception = await Assert.ThrowsAsync<LlmUpstreamException>(() => Stream(new(factory), responses, TestContext.Current.CancellationToken, delivered.Add)); Assert.Equal(error, exception.ResponseBody); Assert.Equal("text/event-stream", exception.ContentType); Assert.DoesNotContain(delivered, x => Ending(x.Data)); Assert.Equal(1, handler.Calls); Assert.True(body.Disposed);
    }
    /// <summary>完整/流式原始字节及终态尾部上限明确失败，不截断为可接受的成功内容。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task RawByteLimit_IsEnforced(bool responses, bool stream)
    {
        var bytes = Bytes(stream ? Text(responses ? "responses.sse" : "chat.sse") : Part(responses ? "responses_result" : "chat_result").GetRawText()); var body = new OllamaBodyStream(bytes);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, stream))); using var factory = new ArkHttpFactory(handler); var provider = new VolcengineLlmProvider(factory, new VolcengineHttpOptions { MaximumBodyBytes = bytes.Length - 1 }); List<LlmJsonEvent> delivered = [];
        await Assert.ThrowsAnyAsync<Exception>(async () => { if (stream) await Stream(provider, responses, TestContext.Current.CancellationToken, delivered.Add); else await Complete(provider, responses, TestContext.Current.CancellationToken); }); Assert.DoesNotContain(delivered, x => Ending(x.Data)); Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>缓冲窗口包含原生 usage/unknown/DONE 控制，不能因终态已出现而绕过上限。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task TailLimit_DoesNotReleasePartialTerminal(bool responses)
    {
        var body = new OllamaBodyStream(Bytes(Text(responses ? "responses.sse" : "chat.sse"))); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); using var factory = new ArkHttpFactory(handler); var provider = new VolcengineLlmProvider(factory, new VolcengineHttpOptions { MaximumBufferedTerminalEvents = 1 }); List<LlmJsonEvent> delivered = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => Stream(provider, responses, TestContext.Current.CancellationToken, delivered.Add)); Assert.DoesNotContain(delivered, x => Ending(x.Data)); Assert.True(body.Disposed);
    }
}
