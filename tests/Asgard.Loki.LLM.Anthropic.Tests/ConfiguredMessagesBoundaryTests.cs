using System.Net;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Anthropic.Tests.AnthropicHttpFixture;
using static Asgard.Loki.LLM.Anthropic.Tests.ConfiguredMessagesFixture;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>明确认证和路径不得削弱原生消息终态、原始错误与拥有资源提交边界。</summary>
public sealed class ConfiguredMessagesBoundaryTests
{
    /// <summary>每个配置分别检查原生结束缺失、重复、后续生成和迟到原生错误。</summary>
    public static IEnumerable<object[]> Controls()
    { foreach (var profile in Enumerable.Range(0, 5)) foreach (var fault in new[] { "missing", "duplicate", "post", "error" }) yield return [profile, fault]; }
    /// <summary>原生错误保持实际状态和原文；无效结束不能泄露此前缓冲的成功帧。</summary>
    [Theory] [MemberData(nameof(Controls))]
    public async Task NativeTerminalFault_DiscardsEnding(int profile, string fault)
    {
        const string error = "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"late\"},\"exact\":9007199254740993}";
        var source = fault == "missing" ? string.Concat(Window.GetProperty("events").EnumerateArray().Where(item => item.GetProperty("type").GetString() != "message_stop").Select(item => Frame(JsonSerializer.Serialize(item)))) : Window.GetProperty("sse").GetString()! + Frame(fault switch { "duplicate" => Stop, "error" => error, _ => "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"late\"}}" });
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new AnthropicHttpFactory(handler); await using var sdk = Build(Provider(factory, profile), profile); List<ClaudeMessageEvent> seen = [];
        if (fault == "error") { var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, true, seen)); Assert.Equal(HttpStatusCode.OK, failure.StatusCode); Assert.Equal(error, failure.ResponseBody); Assert.Equal("text/event-stream", failure.ContentType); } else await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, true, seen)); Assert.DoesNotContain(seen, Ending); Assert.Equal(1, handler.Calls);
    }
    /// <summary>五种认证的完整/流式调用都检查实际 EOF 和最后客户端释放取消。</summary>
    public static IEnumerable<object[]> LateCases()
    { foreach (var row in ConfiguredMessagesTests.Cases()) foreach (var cancel in new[] { false, true }) yield return [row[0], row[1], cancel]; }
    /// <summary>完整读取之后才出现的失败仍阻止完整窗口和流结束交付。</summary>
    [Theory] [MemberData(nameof(LateCases))]
    public async Task LateFailure_PreventsDelivery(int profile, bool stream, bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new OllamaBodyStream(Bytes(stream ? Window.GetProperty("sse").GetString()! : Window.GetProperty("result").GetRawText())) { FailAtEof = !cancel }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, stream))); var factory = new AnthropicHttpFactory(handler) { OnDispose = () => { if (cancel) cancellation.Cancel(); } }; await using var sdk = Build(Provider(factory, profile), profile); List<ClaudeMessageEvent> seen = [];
        var failure = await Record.ExceptionAsync(() => Invoke(sdk, stream, seen, token: cancellation.Token)); Assert.NotNull(failure); if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>非 UTF-8 HTTP 错误保留原始字节和实际媒体，不隐式重试或切换认证。</summary>
    [Theory] [MemberData(nameof(ConfiguredMessagesTests.Cases), MemberType = typeof(ConfiguredMessagesTests))]
    public async Task BinaryHttpError_RemainsOriginal(int profile, bool stream)
    {
        byte[] bytes = [0, 255, 128, 42]; var body = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => { var response = Reply(body, false, status: HttpStatusCode.TooManyRequests); response.Content.Headers.ContentType = new("application/octet-stream"); return Task.FromResult(response); }); var factory = new AnthropicHttpFactory(handler); await using var sdk = Build(Provider(factory, profile), profile); var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, stream, [])); Assert.Equal(bytes, failure.Body.ToArray()); Assert.Equal("application/octet-stream", failure.ContentType); Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>消费者退出只关闭本次流，不进行后台消费。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task EarlyExit_ReleasesOwnedCall(int profile)
    {
        var body = new OllamaBodyStream(Bytes(Window.GetProperty("sse").GetString()!)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new AnthropicHttpFactory(handler); await using var sdk = Build(Provider(factory, profile), profile); await foreach (var item in sdk.LLM.ClaudeMessages.StreamAsync(Request(), new() { SessionId = "configured-messages" }, TestContext.Current.CancellationToken)) { Assert.Equal("message_start", item.Type); break; } Assert.False(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
