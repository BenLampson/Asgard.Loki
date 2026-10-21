using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;
using static Asgard.Loki.LLM.OpenAI.Tests.XaiFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>原生协议窗口不得跳过晚期 EOF、拥有资源关闭或最终取消检查。</summary>
public sealed class XaiResourceTests
{
    /// <summary>结束帧和控制标记之后的读取/最终取消仍撤回成功尾部。</summary>
    [Theory] [InlineData("chat", false)] [InlineData("chat", true)] [InlineData("responses", false)] [InlineData("responses", true)]
    public async Task LateFault_DiscardsNativeEndings(string mode, bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new OllamaBodyStream(Bytes(Part(mode).GetProperty("sse").GetString()!)) { FailAtEof = !cancel }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler) { OnDispose = () => { if (cancel) cancellation.Cancel(); } }; await using var sdk = Build(factory, mode); List<LlmJsonEvent> seen = [];
        var failure = await Record.ExceptionAsync(() => Invoke(sdk, mode, true, seen, cancellationToken: cancellation.Token)); Assert.NotNull(failure); if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>提前离开枚举只释放当前调用，不重试、补终态或在后台读取剩余流。</summary>
    [Theory] [InlineData("chat")] [InlineData("responses")]
    public async Task EarlyExit_ReleasesOwnedCall(string mode)
    {
        var body = new OllamaBodyStream(Bytes(Part(mode).GetProperty("sse").GetString()!)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, mode); var call = new LlmCallOptions { SessionId = "xai" }; var token = TestContext.Current.CancellationToken;
        IAsyncEnumerable<LlmJsonEvent> events = mode == "chat" ? sdk.LLM.ChatCompletions.StreamAsync((ChatCompletionRequest)Request(mode), call, token) : sdk.LLM.Responses.StreamAsync((ResponseRequest)Request(mode), call, token);
        await foreach (var item in events) { Assert.False(Ending(item)); break; } Assert.False(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
