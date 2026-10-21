using System.Net;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Anthropic.Tests.AnthropicHttpFixture;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>智谱原生 Messages 的错误、资源、原生终态与跨实例完整历史。</summary>
public sealed class ZhipuV4MessagesBoundaryTests
{
    private static JsonElement Window => Golden("zhipu-v4-compatible");
    private static ClaudeMessageRequest Request() => Window.GetProperty("caller").Deserialize<ClaudeMessageRequest>()!;
    private static AnthropicLlmProvider Provider(AnthropicHttpFactory factory) => new(factory, id: "zhipu-messages", httpClientName: "Explicit.Zhipu.Messages");
    private static LlmTarget NativeTarget(AnthropicLlmProvider provider) => Target(provider, Window.GetProperty("profiles")[0].GetProperty("root").GetString()!, Window.GetProperty("model").GetString()!);
    /// <summary>相同目标跨实例复用时发送调用者完整原始历史，不拼接隐藏工具状态。</summary>
    [Fact]
    public async Task SameBinding_ReplaysHistory()
    {
        using var handler = new StubHttpHandler(async (message, token) => { Assert.True(JsonElement.DeepEquals(Window.GetProperty("wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Window.GetProperty("result").GetRawText())), false); }); var factory = new AnthropicHttpFactory(handler); var store = Store();
        for (var i = 0; i < 2; i++) { var provider = Provider(factory); await using var sdk = Build(provider, store, NativeTarget(provider)); await sdk.LLM.ClaudeMessages.CreateAsync(Request(), new() { SessionId = "z-messages" }, TestContext.Current.CancellationToken); } Assert.Equal(2, handler.Calls);
    }
    /// <summary>实际 EOF 失败或客户端释放取消仍撤回 message_delta 和 message_stop。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LateFailure_DiscardsEnding(bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new OllamaBodyStream(Bytes(Window.GetProperty("sse").GetString()!)) { FailAtEof = !cancel }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new AnthropicHttpFactory(handler) { OnDispose = () => { if (cancel) cancellation.Cancel(); } }; var provider = Provider(factory); await using var sdk = Build(provider, target: NativeTarget(provider)); List<ClaudeMessageEvent> seen = [];
        var failure = await Record.ExceptionAsync(async () => { await foreach (var item in sdk.LLM.ClaudeMessages.StreamAsync(Request(), new() { SessionId = "z-messages" }, cancellation.Token)) seen.Add(item); }); Assert.NotNull(failure); if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure); Assert.DoesNotContain(seen, e => e.Type is "message_delta" or "message_stop"); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>消费者提前退出仅关闭当前调用，无后台枚举或再次请求。</summary>
    [Fact]
    public async Task EarlyExit_ReleasesOwnedCall()
    {
        var body = new OllamaBodyStream(Bytes(Window.GetProperty("sse").GetString()!)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new AnthropicHttpFactory(handler); var provider = Provider(factory); await using var sdk = Build(provider, target: NativeTarget(provider));
        await foreach (var item in sdk.LLM.ClaudeMessages.StreamAsync(Request(), new() { SessionId = "z-messages" }, TestContext.Current.CancellationToken)) { Assert.Equal("message_start", item.Type); break; } Assert.False(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生 HTTP 错误状态和完整正文保持原文，不隐式重试或切换认证。</summary>
    [Theory] [InlineData(false, 400)] [InlineData(true, 400)] [InlineData(false, 429)] [InlineData(true, 503)]
    public async Task NativeError_IsOriginal(bool stream, int status)
    {
        const string source = "{\"error\":{\"code\":\"1210\",\"message\":\"原生拒绝\"},\"exact\":9007199254740993}"; var body = new OllamaBodyStream(Bytes(source)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, false, status: (HttpStatusCode)status))); var factory = new AnthropicHttpFactory(handler); var provider = Provider(factory); await using var sdk = Build(provider, target: NativeTarget(provider));
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { if (!stream) await sdk.LLM.ClaudeMessages.CreateAsync(Request(), new() { SessionId = "z-messages" }, cancellationToken: TestContext.Current.CancellationToken); else await foreach (var item in sdk.LLM.ClaudeMessages.StreamAsync(Request(), new() { SessionId = "z-messages" }, cancellationToken: TestContext.Current.CancellationToken)) { } }); Assert.Equal((HttpStatusCode)status, failure.StatusCode); Assert.Equal(source, failure.ResponseBody); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
