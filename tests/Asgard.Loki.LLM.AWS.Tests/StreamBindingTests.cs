using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>完整 JSON 与二进制流跨客户端恢复相同完整上游，短期凭据轮换不重调度。</summary>
public sealed class StreamBindingTests
{
    /// <summary>两种调用顺序均保持首次选中的 Runtime/ARN/主体和来源。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CrossModeRecovery_PreservesWholeTarget(bool firstStream)
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); source.Resolve = (principal, _) => ValueTask.FromResult(new AwsLlmSigningCredentials(principal, "AKID" + source.Calls, "fixture-secret", clock.Now.AddHours(1), "token-" + source.Calls)); var store = AwsFixture.Store(); var second = AwsFixture.Target("second") with { UpstreamModel = "arn:aws:bedrock:us-east-1:123456789012:inference-profile/other" }; using var handler = new StubHttpHandler((request, _) => { Assert.Equal("second.invalid", request.RequestUri!.Host); Assert.Contains("inference-profile%2Fother/", request.RequestUri.AbsoluteUri); Assert.Contains("Credential=AKID" + source.Calls + "/", Assert.Single(request.Headers.GetValues("Authorization"))); Assert.Equal("token-" + source.Calls, Assert.Single(request.Headers.GetValues("x-amz-security-token"))); return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("invoke-with-response-stream", StringComparison.Ordinal) ? StreamFixture.Reply(StreamFixture.Bytes()) : AwsFixture.Reply()); }); var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock); var targets = new[] { AwsFixture.Target(), second };
        async Task Invoke(Asgard.Loki.Abstractions.ILokiClient client, bool stream) { if (stream) { await foreach (var _ in client.LLM.ClaudeMessages.StreamAsync(AwsFixture.Request().Deserialize<ClaudeMessageRequest>()!, new() { SessionId = "aws-session" }, TestContext.Current.CancellationToken)) { } } else await AwsFixture.Invoke(client); }
        await using (var first = AwsFixture.Client(provider, store, targets, 1)) await Invoke(first, firstStream); var before = await store.GetAsync("aws-session", TestContext.Current.CancellationToken); clock.Now = clock.Now.AddMinutes(30); await using (var next = AwsFixture.Client(provider, store, targets)) await Invoke(next, !firstStream); var after = await store.GetAsync("aws-session", TestContext.Current.CancellationToken); Assert.Equal(before!.Fingerprint, after!.Fingerprint); var json = JsonSerializer.Serialize(after); Assert.DoesNotContain("AKID", json); Assert.DoesNotContain("fixture-secret", json); Assert.DoesNotContain("token-", json); Assert.Equal(2, source.Calls); Assert.Equal(2, handler.Calls); Assert.All(factory.Clients, client => Assert.True(client.Disposed)); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
}
