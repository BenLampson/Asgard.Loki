using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>稳定 External 主体/完整 Runtime 目标与短期签名 Key 的生命周期分离。</summary>
public sealed class BindingTests
{
    /// <summary>Key/Token 轮换跨客户端不会改变首次选择的服务器和实际 ARN。</summary>
    [Fact]
    public async Task CredentialRotation_PreservesWholeTarget()
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); source.Resolve = (principal, _) => ValueTask.FromResult(new AwsLlmSigningCredentials(principal, "AKID" + source.Calls, "fixture-secret-" + source.Calls, clock.Now.AddHours(1), "fixture-token-" + source.Calls)); var store = AwsFixture.Store(); var second = AwsFixture.Target("second") with { UpstreamModel = "arn:aws:bedrock:us-east-1:123456789012:inference-profile/profile" };
        using var handler = new StubHttpHandler((request, _) => { Assert.Equal("second.invalid", request.RequestUri!.Host); Assert.Contains("inference-profile%2Fprofile", request.RequestUri.AbsoluteUri); Assert.Contains("Credential=AKID" + source.Calls + "/", Assert.Single(request.Headers.GetValues("Authorization"))); Assert.Equal("fixture-token-" + source.Calls, Assert.Single(request.Headers.GetValues("x-amz-security-token"))); return Task.FromResult(AwsFixture.Reply()); }); var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock); var targets = new[] { AwsFixture.Target(), second };
        await using (var client = AwsFixture.Client(provider, store, targets, 1)) await AwsFixture.Invoke(client); var before = await store.GetAsync("aws-session", TestContext.Current.CancellationToken); clock.Now = clock.Now.AddMinutes(30); await using (var client = AwsFixture.Client(provider, store, targets)) await AwsFixture.Invoke(client); var after = await store.GetAsync("aws-session", TestContext.Current.CancellationToken); Assert.Equal(before!.Fingerprint, after!.Fingerprint); var serialized = JsonSerializer.Serialize(after); Assert.DoesNotContain("AKID", serialized); Assert.DoesNotContain("fixture-", serialized); Assert.DoesNotContain("principal-a", serialized); Assert.Equal(2, source.Calls); Assert.Equal(2, handler.Calls); Assert.All(factory.Clients, client => Assert.True(client.Disposed)); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
    /// <summary>完整目标、主体或配置变化必须在再次获取凭据前使旧 Session 失败。</summary>
    [Theory] [InlineData("model")] [InlineData("server")] [InlineData("prefix")] [InlineData("principal")] [InlineData("source")] [InlineData("region")] [InlineData("version")] [InlineData("headers")] [InlineData("limit")] [InlineData("client")]
    public async Task ChangedIdentityOrConfiguration_RejectsOldSession(string change)
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); var store = AwsFixture.Store(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(AwsFixture.Reply())); var factory = new AwsHttpFactory(handler); await using (var first = AwsFixture.Client(new(factory, source, new() { Region = "us-east-1" }, timeProvider: clock), store)) await AwsFixture.Invoke(first); var target = AwsFixture.Target() with { UpstreamModel = change == "model" ? "us.anthropic.other-model:0" : AwsFixture.Model }; if (change is "server" or "prefix") target = target with { BaseAddress = new(change == "server" ? "https://other.invalid/proxy/" : "https://first.invalid/other/") }; if (change == "source") source.Version = "changed-source";
        var options = new AwsLlmOptions { Region = change == "region" ? "us-west-2" : "us-east-1", ClaudeAnthropicVersion = change == "version" ? "explicit-other-version" : "bedrock-2023-05-31", MaximumBodyBytes = change == "limit" ? 1024 : 32 * 1024 * 1024, RequestHeaders = change == "headers" ? new Dictionary<string, string> { ["x-amzn-bedrock-trace"] = "ENABLED" } : new Dictionary<string, string>() }; var provider = new AwsLlmProvider(factory, source, options, httpClientName: change == "client" ? "other" : "Asgard.Loki.LLM.AWS", timeProvider: clock); await using var next = AwsFixture.Client(provider, store, [target], credential: LlmCredential.External("identity", change == "principal" ? "principal-b" : "principal-a")); await Assert.ThrowsAsync<InvalidOperationException>(() => AwsFixture.Invoke(next)); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    /// <summary>主体/期限/来源漂移、空结果和来源异常均不重取或发送生成。</summary>
    [Theory] [InlineData("principal")] [InlineData("expired")] [InlineData("null")] [InlineData("throw")] [InlineData("drift-before")] [InlineData("drift-during")]
    public async Task InvalidCredentials_DoNotRetry(string failure)
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); source.Resolve = (principal, _) => { if (failure == "throw") throw new InvalidOperationException("source failure"); if (failure == "drift-during") source.Version = "changed"; return ValueTask.FromResult(failure == "null" ? null! : new AwsLlmSigningCredentials(failure == "principal" ? "other" : principal, AwsCredentialSource.KeyId, AwsCredentialSource.Secret, clock.Now.AddMinutes(failure == "expired" ? 0 : 60))); }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(AwsFixture.Reply())); var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock); if (failure == "drift-before") source.Version = "changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(AwsFixture.Request(), AwsFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(failure == "drift-before" ? 0 : 1, source.Calls); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>取消、发送前凭据到期或来源变化均不签名后重试。</summary>
    [Theory] [InlineData("before")] [InlineData("source")] [InlineData("factory")] [InlineData("expiry")] [InlineData("drift")]
    public async Task BeforeSendFault_DoesNotDispatch(string phase)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var clock = new AwsClock(); var source = new AwsCredentialSource(clock); if (phase == "before") cancel.Cancel(); if (phase == "source") source.Resolve = (principal, _) => { cancel.Cancel(); return ValueTask.FromResult(new AwsLlmSigningCredentials(principal, AwsCredentialSource.KeyId, AwsCredentialSource.Secret, clock.Now.AddHours(1))); }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(AwsFixture.Reply())); var factory = new AwsHttpFactory(handler) { OnCreate = () => { if (phase == "expiry") clock.Now = clock.Now.AddHours(2); else if (phase == "drift") source.Version = "changed"; else cancel.Cancel(); } }; var provider = new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock);
        if (phase is "expiry" or "drift") await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(AwsFixture.Request(), AwsFixture.Context(), cancel.Token)); else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CreateAsync(AwsFixture.Request(), AwsFixture.Context(), cancel.Token)); Assert.Equal(phase == "before" ? 0 : 1, source.Calls); Assert.Equal(0, handler.Calls); Assert.All(factory.Clients, client => Assert.True(client.Disposed));
    }
    /// <summary>签名时间取自最后发送阶段，等待工厂不会留下过时的日期头。</summary>
    [Fact]
    public async Task SigningDate_IsCapturedAfterFactoryCreation()
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler((request, _) => { Assert.Equal("20150830T123645Z", Assert.Single(request.Headers.GetValues("x-amz-date"))); return Task.FromResult(AwsFixture.Reply()); }); var factory = new AwsHttpFactory(handler) { OnCreate = () => clock.Now = clock.Now.AddSeconds(45) }; await new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock).CreateAsync(AwsFixture.Request(), AwsFixture.Context(), TestContext.Current.CancellationToken); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
}
