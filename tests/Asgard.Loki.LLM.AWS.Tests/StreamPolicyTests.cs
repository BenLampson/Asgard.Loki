using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>二进制状态和解析策略均有限，配置变化参与完整 Session 绑定。</summary>
public sealed class StreamPolicyTests
{
    private static AwsLlmOptions Options(string field, bool invalid = false) => new()
    {
        Region = "us-east-1", MaximumEventStreamFrameBytes = field == "frame" ? invalid ? 15 : 16 : 8 * 1024 * 1024,
        MaximumEventStreamHeaders = field == "headers" ? invalid ? 0 : 2 : 128,
        MaximumBufferedTerminalEvents = field == "tail" ? invalid ? 0 : 2 : 128,
        MaximumTrackedClaudeBlocks = field == "blocks" ? invalid ? 0 : 1 : 128
    };
    /// <summary>单帧、头数、尾部和关联数超限均失败，不通过跳过字段或截断来完成。</summary>
    [Theory] [InlineData("frame")] [InlineData("headers")] [InlineData("tail")] [InlineData("blocks")]
    public async Task LocalPolicies_RejectOverflow(string field)
    {
        var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(StreamFixture.Bytes()))); var factory = new AwsHttpFactory(handler); var finished = false; await Assert.ThrowsAsync<InvalidDataException>(() => StreamFixture.Consume(new(factory, new AwsCredentialSource(clock), Options(field), timeProvider: clock), TestContext.Current.CancellationToken, item => finished |= item.Type is "message_delta" or "message_stop")); Assert.False(finished); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>策略变化使旧完整调用 Session 失效，不能重新获取凭据或选择目标。</summary>
    [Theory] [InlineData("frame")] [InlineData("headers")] [InlineData("tail")] [InlineData("blocks")]
    public async Task ChangedPolicy_InvalidatesSession(string field)
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); var store = AwsFixture.Store(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(AwsFixture.Reply())); var factory = new AwsHttpFactory(handler); await using (var first = AwsFixture.Client(new(factory, source, new() { Region = "us-east-1" }, timeProvider: clock), store)) await AwsFixture.Invoke(first); await using var next = AwsFixture.Client(new(factory, source, Options(field), timeProvider: clock), store); await Assert.ThrowsAsync<InvalidOperationException>(() => AwsFixture.Invoke(next)); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    /// <summary>非法本地资源策略在构造时拒绝。</summary>
    [Theory] [InlineData("frame")] [InlineData("headers")] [InlineData("tail")] [InlineData("blocks")]
    public void InvalidLocalPolicy_IsRejected(string field)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); Assert.ThrowsAny<ArgumentException>(() => new AwsLlmProvider(new AwsHttpFactory(handler), new AwsCredentialSource(new()), Options(field, invalid: true)));
    }
    /// <summary>非 JSON 的显式模型 Accept 在取凭据之前失败，不覆盖调用者配置掩盖冲突。</summary>
    [Fact]
    public async Task ConflictingModelAccept_FailsBeforeSource()
    {
        var source = new AwsCredentialSource(new()); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new AwsHttpFactory(handler); await Assert.ThrowsAsync<ArgumentException>(() => StreamFixture.Consume(new(factory, source, new() { Region = "us-east-1", RequestHeaders = new Dictionary<string, string> { ["x-amzn-bedrock-accept"] = "text/plain" } }), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
}
