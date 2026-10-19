using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>Converse 策略有独立块上限，默认省略不改变已有 AWS 绑定。</summary>
public sealed class ConverseStreamPolicyTests
{
    /// <summary>精确旧默认字节保持 JSON/Claude/Converse 先前 Session 可继续使用。</summary>
    [Fact]
    public void DefaultBinding_RetainsPreConverseStreamBytes()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var provider = new AwsLlmProvider(new AwsHttpFactory(handler), new AwsCredentialSource(new()), new() { Region = "us-east-1" }); Assert.Equal(typeof(AwsLlmProvider).AssemblyQualifiedName + ":aws-claude-eventstream-v1:" + """{"Options":{"Region":"us-east-1","ClaudeAnthropicVersion":"bedrock-2023-05-31","MaximumBodyBytes":33554432,"RequestHeaders":{},"MaximumEventStreamFrameBytes":8388608,"MaximumEventStreamHeaders":128,"MaximumBufferedTerminalEvents":128,"MaximumTrackedClaudeBlocks":128},"Client":"Asgard.Loki.LLM.AWS","Source":"fixture-source-v1"}""", provider.BindingIdentity);
    }
    /// <summary>单独的 Converse 块数和已有帧/头/尾部上限均能阻止超限成功。</summary>
    [Theory] [InlineData("blocks")] [InlineData("frame")] [InlineData("headers")] [InlineData("tail")]
    public async Task ResourceLimits_AreEnforced(string field)
    {
        var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(ConverseStreamFixture.Bytes()))); var factory = new AwsHttpFactory(handler); var options = new AwsLlmOptions { Region = "us-east-1", MaximumTrackedConverseBlocks = field == "blocks" ? 1 : null, MaximumEventStreamFrameBytes = field == "frame" ? 16 : 8 * 1024 * 1024, MaximumEventStreamHeaders = field == "headers" ? 1 : 128, MaximumBufferedTerminalEvents = field == "tail" ? 1 : 128 }; var finished = false; await Assert.ThrowsAsync<InvalidDataException>(() => ConverseStreamFixture.Consume(new(factory, new AwsCredentialSource(clock), options, timeProvider: clock), TestContext.Current.CancellationToken, item => finished |= item.EventName == "messageStop")); Assert.False(finished); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>新增的显式块策略使旧绑定失败，不再次获取凭据。</summary>
    [Fact]
    public async Task ExplicitBlockPolicy_InvalidatesPreviousSession()
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); var store = AwsFixture.Store(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(ConverseFixture.Reply())); var factory = new AwsHttpFactory(handler); await using (var first = ConverseFixture.Client(new(factory, source, new() { Region = "us-east-1" }, timeProvider: clock), store)) await ConverseFixture.Invoke(first); await using var next = ConverseFixture.Client(new(factory, source, new() { Region = "us-east-1", MaximumTrackedConverseBlocks = 1 }, timeProvider: clock), store); await Assert.ThrowsAsync<InvalidOperationException>(() => ConverseFixture.Invoke(next)); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    /// <summary>非法新增策略在构造时拒绝。</summary>
    [Theory] [InlineData(0)] [InlineData(-1)]
    public void InvalidBlockPolicy_IsRejected(int limit)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); Assert.ThrowsAny<ArgumentException>(() => new AwsLlmProvider(new AwsHttpFactory(handler), new AwsCredentialSource(new()), new() { Region = "us-east-1", MaximumTrackedConverseBlocks = limit }));
    }
    /// <summary>JSON 和流式可以跨客户端恢复相同完整目标，短期 Key/Token 轮换不改变绑定。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CrossModeSessionRecovery_RetainsTargetAndPrincipal(bool firstStream)
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); source.Resolve = (principal, _) => ValueTask.FromResult(new AwsLlmSigningCredentials(principal, "AKID" + source.Calls, "fixture-secret", clock.Now.AddHours(1), "token-" + source.Calls)); var store = AwsFixture.Store(); var second = ConverseFixture.Target("second") with { UpstreamModel = "vendor.actual-model:0" }; using var handler = new StubHttpHandler((request, _) => { Assert.Equal("second.invalid", request.RequestUri!.Host); Assert.Contains("vendor.actual-model%3A0/converse", request.RequestUri.AbsoluteUri); Assert.Contains("Credential=AKID" + source.Calls + "/", Assert.Single(request.Headers.GetValues("Authorization"))); Assert.Equal("token-" + source.Calls, Assert.Single(request.Headers.GetValues("x-amz-security-token"))); return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("converse-stream", StringComparison.Ordinal) ? StreamFixture.Reply(ConverseStreamFixture.Bytes()) : ConverseFixture.Reply()); }); var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock); var targets = new[] { ConverseFixture.Target(), second };
        async Task Invoke(Asgard.Loki.Abstractions.ILokiClient client, bool stream) { if (stream) { await foreach (var _ in client.LLM.BedrockConverse.StreamAsync(ConverseFixture.Request().Deserialize<BedrockConverseRequest>()!, new() { SessionId = "converse-session" }, TestContext.Current.CancellationToken)) { } } else await ConverseFixture.Invoke(client); }
        await using (var first = ConverseFixture.Client(provider, store, targets, 1)) await Invoke(first, firstStream); var before = await store.GetAsync("converse-session", TestContext.Current.CancellationToken); clock.Now = clock.Now.AddMinutes(30); await using (var next = ConverseFixture.Client(provider, store, targets)) await Invoke(next, !firstStream); var after = await store.GetAsync("converse-session", TestContext.Current.CancellationToken); Assert.Equal(before!.Fingerprint, after!.Fingerprint); Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(after)); Assert.Equal(2, source.Calls); Assert.Equal(2, handler.Calls); Assert.All(factory.Clients, client => Assert.True(client.Disposed)); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
}
