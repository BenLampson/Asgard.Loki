using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>Nova 的新有界状态选项显式绑定，同时保持前一版默认绑定字节不变。</summary>
public sealed class NovaStreamPolicyTests
{
    /// <summary>原始帧、类型化头、终态尾部和内容块超限均明确失败，不能跳过字段。</summary>
    [Theory] [InlineData("frame")] [InlineData("headers")] [InlineData("tail")] [InlineData("blocks")]
    public async Task LocalLimits_FailWithoutTrimming(string field)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(NovaStreamFixture.Bytes()))); List<BedrockNovaEvent> seen = [];
        var options = new AwsLlmOptions { Region = "us-east-1", MaximumEventStreamFrameBytes = field == "frame" ? 16 : 8 * 1024 * 1024, MaximumEventStreamHeaders = field == "headers" ? 2 : 128, MaximumBufferedTerminalEvents = field == "tail" ? 1 : 128, MaximumTrackedNovaBlocks = field == "blocks" ? 1 : null };
        await Assert.ThrowsAsync<InvalidDataException>(() => NovaStreamFixture.Drain(new(new AwsHttpFactory(handler), options), TestContext.Current.CancellationToken, seen, apiKey: true)); Assert.DoesNotContain(seen, item => item.Type is "messageStop" or "metadata"); Assert.Equal(1, handler.Calls);
    }
    /// <summary>新增可空字段默认省略，SigV4/Bearer 的历史默认身份字节不变。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void DefaultIdentity_PreservesPreviousOptionShell(bool apiKey)
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new AwsHttpFactory(handler); var options = new AwsLlmOptions { Region = "us-east-1" };
        var oldOptions = JsonSerializer.SerializeToNode(options)!.AsObject(); Assert.False(oldOptions.ContainsKey("MaximumTrackedNovaBlocks"));
        var provider = apiKey ? new AwsLlmProvider(factory, options) : new AwsLlmProvider(factory, source, options, timeProvider: clock);
        var expected = typeof(AwsLlmProvider).AssemblyQualifiedName + (apiKey ? ":aws-bedrock-api-key-v1:" : ":aws-claude-eventstream-v1:") + JsonSerializer.Serialize(new { Options = oldOptions, Client = "Asgard.Loki.LLM.AWS", Source = apiKey ? "bedrock-api-key" : source.BindingIdentity });
        Assert.Equal(expected, provider.BindingIdentity); Assert.Empty(factory.Clients);
    }
    /// <summary>明确的块策略变化使已绑定的 Nova JSON Session 在获取身份或 HTTP 前失效。</summary>
    [Fact]
    public async Task ChangedBlockPolicy_RejectsExistingSession()
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(MantleFixture.Reply(new MemoryStream(MantleFixture.Bytes(NovaFixture.Golden().GetProperty("response").GetRawText())), false))); var factory = new AwsHttpFactory(handler); var store = AwsFixture.Store();
        await using (var first = NovaFixture.Client(new(factory, source, new() { Region = "us-east-1" }, timeProvider: clock), store: store)) await first.LLM.BedrockNova.InvokeAsync(NovaFixture.Typed(), new() { SessionId = "aws-session" }, TestContext.Current.CancellationToken);
        await using var second = NovaFixture.Client(new(factory, source, new() { Region = "us-east-1", MaximumTrackedNovaBlocks = 1 }, timeProvider: clock), store: store);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await foreach (var _ in second.LLM.BedrockNova.StreamAsync(NovaFixture.Typed(), new() { SessionId = "aws-session" }, TestContext.Current.CancellationToken)) { } }); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.Single(factory.Clients);
    }
    /// <summary>普通 key/令牌轮换与实例重建不能改变既有完整目标，JSON 可明确进入同协议流方法。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task JsonThenStream_RestoresCompleteTarget(bool apiKey)
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); var acquired = 0; source.Resolve = (principal, _) => ValueTask.FromResult(new AwsLlmSigningCredentials(principal, ++acquired == 1 ? "AKIDFIRST" : "AKIDSECOND", AwsCredentialSource.Secret, clock.Now.AddHours(1), acquired == 1 ? "token-first" : "token-second"));
        var calls = 0; using var handler = new StubHttpHandler((message, _) =>
        {
            Assert.Equal("second.invalid", message.RequestUri!.Host); Assert.Contains("nova-selected", message.RequestUri.AbsolutePath);
            if (!apiKey) { Assert.Contains(calls == 0 ? "AKIDFIRST" : "AKIDSECOND", Assert.Single(message.Headers.GetValues("Authorization"))); Assert.Equal(calls == 0 ? "token-first" : "token-second", Assert.Single(message.Headers.GetValues("x-amz-security-token"))); }
            var stream = calls++ != 0; return Task.FromResult(stream ? StreamFixture.Reply(NovaStreamFixture.Complete()) : MantleFixture.Reply(new MemoryStream(MantleFixture.Bytes(NovaFixture.Golden().GetProperty("response").GetRawText())), false));
        });
        var factory = new AwsHttpFactory(handler); var options = new AwsLlmOptions { Region = "us-east-1" }; AwsLlmProvider Provider() => apiKey ? new(factory, options) : new(factory, source, options, timeProvider: clock);
        var targets = new[] { NovaFixture.Target(), NovaFixture.Target("nova-selected") with { Id = "second", BaseAddress = new("https://second.invalid/proxy/") } }; var store = AwsFixture.Store();
        await using (var first = NovaFixture.Client(Provider(), apiKey, store, targets, 1)) await first.LLM.BedrockNova.InvokeAsync(NovaFixture.Typed(), new() { SessionId = "aws-session" }, TestContext.Current.CancellationToken);
        await using var second = NovaFixture.Client(Provider(), apiKey, store, targets, 0); List<BedrockNovaEvent> seen = []; await foreach (var item in second.LLM.BedrockNova.StreamAsync(NovaFixture.Typed(), new() { SessionId = "aws-session" }, TestContext.Current.CancellationToken)) { Assert.Equal("second", item.Metadata.TargetId); seen.Add(item); }
        Assert.Contains(seen, item => item.Type == "messageStop"); Assert.Equal(2, handler.Calls); Assert.Equal(apiKey ? 0 : 2, source.Calls); Assert.All(factory.Clients, client => Assert.True(client.Disposed)); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>无效限额与所选非 JSON 模型内容在身份获取前明确失败。</summary>
    [Fact]
    public async Task InvalidPolicyOrModelMedia_FailsBeforeAcquisition()
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new AwsHttpFactory(handler);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AwsLlmProvider(factory, source, new() { Region = "us-east-1", MaximumTrackedNovaBlocks = 0 }));
        var provider = new AwsLlmProvider(factory, source, new() { Region = "us-east-1", RequestHeaders = new Dictionary<string, string> { ["x-amzn-bedrock-accept"] = "application/octet-stream" } }, timeProvider: clock);
        await Assert.ThrowsAsync<ArgumentException>(() => NovaStreamFixture.Drain(provider, TestContext.Current.CancellationToken, [])); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
}
