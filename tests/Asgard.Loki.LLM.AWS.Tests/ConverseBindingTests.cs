using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Conversion;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>Converse 共享完整目标/主体绑定，不共享 Claude 消息语义或隐式选择。</summary>
public sealed class ConverseBindingTests
{
    /// <summary>短期 Key/Token 更新仍恢复首次选择的 Runtime/模型；模型品牌不更换原生 API。</summary>
    [Fact]
    public async Task RefreshedCredentials_RetainWholeConverseTarget()
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); source.Resolve = (principal, _) => ValueTask.FromResult(new AwsLlmSigningCredentials(principal, "AKID" + source.Calls, "fixture-secret", clock.Now.AddHours(1), "token-" + source.Calls)); var store = AwsFixture.Store(); var second = ConverseFixture.Target("second") with { UpstreamModel = "anthropic.configured-converse-model:0" }; using var handler = new StubHttpHandler((request, _) => { Assert.Equal("https://second.invalid/proxy/model/anthropic.configured-converse-model%3A0/converse", request.RequestUri!.AbsoluteUri); Assert.Contains("Credential=AKID" + source.Calls + "/", Assert.Single(request.Headers.GetValues("Authorization"))); Assert.Equal("token-" + source.Calls, Assert.Single(request.Headers.GetValues("x-amz-security-token"))); return Task.FromResult(ConverseFixture.Reply()); }); var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock); var targets = new[] { ConverseFixture.Target(), second };
        await using (var first = ConverseFixture.Client(provider, store, targets, 1)) await ConverseFixture.Invoke(first); var before = await store.GetAsync("converse-session", TestContext.Current.CancellationToken); clock.Now = clock.Now.AddMinutes(30); await using (var next = ConverseFixture.Client(provider, store, targets)) await ConverseFixture.Invoke(next); var after = await store.GetAsync("converse-session", TestContext.Current.CancellationToken); Assert.Equal(before!.Fingerprint, after!.Fingerprint); Assert.DoesNotContain("AKID", JsonSerializer.Serialize(after)); Assert.Equal(2, source.Calls); Assert.Equal(2, handler.Calls); Assert.All(factory.Clients, client => Assert.True(client.Disposed)); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
    /// <summary>实际模型/Runtime/主体/源/区域等改变，旧 Session 不再获取凭据或转移目标。</summary>
    [Theory] [InlineData("model")] [InlineData("server")] [InlineData("principal")] [InlineData("source")] [InlineData("region")] [InlineData("headers")] [InlineData("limit")]
    public async Task ChangedBinding_RejectsBeforeSource(string field)
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); var store = AwsFixture.Store(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(ConverseFixture.Reply())); var factory = new AwsHttpFactory(handler); await using (var first = ConverseFixture.Client(new(factory, source, new() { Region = "us-east-1" }, timeProvider: clock), store)) await ConverseFixture.Invoke(first); var target = ConverseFixture.Target() with { UpstreamModel = field == "model" ? "amazon.other:0" : ConverseFixture.Model, BaseAddress = field == "server" ? new("https://other.invalid/") : ConverseFixture.Target().BaseAddress }; if (field == "source") source.Version = "changed"; var provider = new AwsLlmProvider(factory, source, new() { Region = field == "region" ? "us-west-2" : "us-east-1", MaximumBodyBytes = field == "limit" ? 1024 : 32 * 1024 * 1024, RequestHeaders = field == "headers" ? new Dictionary<string, string> { ["x-amzn-bedrock-trace"] = "ENABLED" } : [] }, timeProvider: clock); await using var next = ConverseFixture.Client(provider, store, [target], credential: LlmCredential.External("identity", field == "principal" ? "other" : "principal-a")); await Assert.ThrowsAsync<InvalidOperationException>(() => ConverseFixture.Invoke(next)); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    /// <summary>同一 Session 不能从 Converse 改为 Claude，即使显式声明目标支持两种协议。</summary>
    [Fact]
    public async Task SessionProtocol_CannotChangeToClaude()
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(ConverseFixture.Reply())); var factory = new AwsHttpFactory(handler); await using var client = ConverseFixture.Client(new(factory, source, new() { Region = "us-east-1" }, timeProvider: clock), targets: [ConverseFixture.Target() with { Protocols = LlmProtocol.BedrockConverse | LlmProtocol.ClaudeMessages }]); await ConverseFixture.Invoke(client); await Assert.ThrowsAsync<InvalidOperationException>(() => client.LLM.ClaudeMessages.CreateAsync(AwsFixture.Request().Deserialize<ClaudeMessageRequest>()!, new() { SessionId = "converse-session" }, TestContext.Current.CancellationToken)); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    /// <summary>新原生协议不会自动进入既有四协议转换声明。</summary>
    [Fact]
    public void ConversionWrapper_DoesNotAdvertiseConverse()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var native = new AwsLlmProvider(new AwsHttpFactory(handler), new AwsCredentialSource(new()), new() { Region = "us-east-1" }); var converted = new ConvertingLlmProvider(native, LlmProtocol.ClaudeMessages, "converted-aws"); Assert.Throws<ArgumentException>(() => new LokiClientBuilder().AddLLM(new() { Providers = [converted], Credentials = [LlmCredential.External("identity", "principal-a")], SessionStore = AwsFixture.Store(), Routes = [new() { Model = "public", Targets = [ConverseFixture.Target() with { ProviderId = converted.Id }] }] }).Build());
    }
    /// <summary>首次凭据等待之前拍摄原生请求快照，不把等待期间修改发送出去。</summary>
    [Fact]
    public async Task ConverseSnapshot_PrecedesSourceAwait()
    {
        var clock = new AwsClock(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var source = new AwsCredentialSource(clock) { Resolve = async (principal, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return new(principal, AwsCredentialSource.KeyId, AwsCredentialSource.Secret, clock.Now.AddHours(1)); } }; using var handler = new StubHttpHandler(async (request, token) => { Assert.True(JsonNode.DeepEquals(JsonNode.Parse(AwsFixture.Text("converse-wire")), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return ConverseFixture.Reply(); }); var input = ConverseFixture.Request(); var call = new AwsLlmProvider(new AwsHttpFactory(handler), source, new() { Region = "us-east-1" }, timeProvider: clock).ConverseAsync(input, ConverseFixture.Context(), TestContext.Current.CancellationToken); await entered.Task.WaitAsync(TestContext.Current.CancellationToken); input["messages"] = new JsonArray(); release.SetResult(); await call; Assert.Equal(1, handler.Calls);
    }
}
