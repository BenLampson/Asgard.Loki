using System.Text;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>MaaS SSE 状态有限且绑定策略不能在同一 Session 内悄悄漂移。</summary>
public sealed class MaaSStreamPolicyTests
{
    /// <summary>选择终态暂存/choice/tool 数量边界而不按稀疏原始索引扩张数组。</summary>
    [Theory] [InlineData("tail")] [InlineData("choice")] [InlineData("tool")]
    public async Task NativeStateLimits_AreEnforced(string limit)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var text = limit == "choice" ? MaaSStreamFixture.Frame(MaaSStreamFixture.Start) + MaaSStreamFixture.Frame("{\"choices\":[{\"index\":2147483647,\"delta\":{}}]}") : MaaSStreamFixture.Sse(); var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(text)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(body))); var factory = new VertexHttpFactory(handler); var options = limit switch { "tail" => new GoogleVertexMaaSLlmOptions { MaximumBufferedTerminalEvents = 2 }, "choice" => new() { MaximumTrackedChoices = 1 }, _ => new() { MaximumTrackedToolCalls = 1 } }; var terminal = false; await Assert.ThrowsAsync<InvalidDataException>(() => MaaSStreamFixture.Consume(new(factory, source, options, timeProvider: clock), TestContext.Current.CancellationToken, item => { terminal |= item.Data.TryGetProperty("choices", out var choices) && choices.EnumerateArray().Any(choice => choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == System.Text.Json.JsonValueKind.String); })); Assert.False(terminal); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>流式策略变化使先前完整调用绑定失效，不再次获取令牌。</summary>
    [Theory] [InlineData("tail")] [InlineData("choice")] [InlineData("tool")]
    public async Task ChangedPolicy_RejectsExistingSession(string limit)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var store = VertexFixture.Store(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(MaaSFixture.Reply())); var factory = new VertexHttpFactory(handler); await using (var first = MaaSFixture.Client(new(factory, source, timeProvider: clock), store)) await MaaSFixture.Invoke(first); var options = limit switch { "tail" => new GoogleVertexMaaSLlmOptions { MaximumBufferedTerminalEvents = 2 }, "choice" => new() { MaximumTrackedChoices = 1 }, _ => new() { MaximumTrackedToolCalls = 1 } }; await using var next = MaaSFixture.Client(new(factory, source, options, timeProvider: clock), store); await Assert.ThrowsAsync<InvalidOperationException>(() => MaaSFixture.Invoke(next)); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    /// <summary>零状态上限在构造时直接拒绝。</summary>
    [Theory] [InlineData("tail")] [InlineData("choice")] [InlineData("tool")]
    public void InvalidPolicy_IsRejected(string limit)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var options = limit switch { "tail" => new GoogleVertexMaaSLlmOptions { MaximumBufferedTerminalEvents = 0 }, "choice" => new() { MaximumTrackedChoices = 0 }, _ => new() { MaximumTrackedToolCalls = 0 } }; Assert.ThrowsAny<ArgumentException>(() => new GoogleVertexMaaSLlmProvider(new VertexHttpFactory(handler), new VertexTokenSource(new()), options));
    }
}
