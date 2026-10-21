using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.AWS.Tests.MantleFixture;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>Mantle 的稳定来源/完整目标绑定与短期签名材料相互独立。</summary>
public sealed class MantleBindingTests
{
    /// <summary>正文和选项头在来源等待之前独立复制，后续修改不改变签名或请求。</summary>
    [Fact]
    public async Task Snapshot_PrecedesCredentialAwait()
    {
        var clock = new AwsClock(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new AwsCredentialSource(clock) { Resolve = async (principal, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return new(principal, AwsCredentialSource.KeyId, AwsCredentialSource.Secret, clock.Now.AddHours(1)); } };
        var input = Request(); var original = input.DeepClone().AsObject(); original["model"] = Target().UpstreamModel; original["stream"] = false;
        var headers = new Dictionary<string, string> { ["anthropic-beta"] = "original" };
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.True(JsonNode.DeepEquals(original, JsonNode.Parse(await message.Content!.ReadAsStringAsync(token)))); Assert.Equal("original", Assert.Single(message.Headers.GetValues("anthropic-beta")));
            Assert.Contains("anthropic-beta", Assert.Single(message.Headers.GetValues("Authorization"))); return Reply(new MemoryStream(Bytes("{\"id\":\"msg\"}")), false);
        });
        var factory = new AwsHttpFactory(handler); var provider = new AwsMantleLlmProvider(factory, source, new() { Region = "us-east-1", RequestHeaders = headers }, timeProvider: clock); var binding = provider.BindingIdentity;
        var call = provider.CreateAsync(input, Context(), TestContext.Current.CancellationToken); await entered.Task.WaitAsync(TestContext.Current.CancellationToken); input["messages"] = new JsonArray(); headers["anthropic-beta"] = "changed"; release.SetResult(); await call;
        Assert.Equal(binding, provider.BindingIdentity); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    /// <summary>稳定来源、区域、版本、头和资源策略变化不能沿用原 Session。</summary>
    [Theory]
    [InlineData("region")] [InlineData("version")] [InlineData("header")] [InlineData("source")]
    [InlineData("client")] [InlineData("bytes")] [InlineData("tail")] [InlineData("blocks")] [InlineData("principal")]
    public async Task ChangedBinding_FailsBeforeSecondAcquisition(string change)
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes("{\"id\":\"msg\"}")), false))); var factory = new AwsHttpFactory(handler); var store = AwsFixture.Store();
        await using (var first = Client(new AwsMantleLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock), store))
            await first.LLM.ClaudeMessages.CreateAsync(Request().Deserialize<ClaudeMessageRequest>()!, new() { SessionId = "mantle-session" }, TestContext.Current.CancellationToken);
        if (change == "source") source.Version = "other-source";
        var options = new AwsMantleLlmOptions { Region = change == "region" ? "us-west-2" : "us-east-1", AnthropicVersion = change == "version" ? "2024-01-01" : "2023-06-01", MaximumBodyBytes = change == "bytes" ? 1024 : 32 * 1024 * 1024, MaximumBufferedTerminalEvents = change == "tail" ? 2 : 128, MaximumTrackedContentBlocks = change == "blocks" ? 2 : 128, RequestHeaders = change == "header" ? new Dictionary<string, string> { ["anthropic-beta"] = "explicit" } : new Dictionary<string, string>() };
        await using var second = Client(new AwsMantleLlmProvider(factory, source, options, httpClientName: change == "client" ? "other" : "Asgard.Loki.LLM.AWS.Mantle", timeProvider: clock), store, principal: change == "principal" ? "other" : "principal-a");
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.ClaudeMessages.CreateAsync(Request().Deserialize<ClaudeMessageRequest>()!, new() { SessionId = "mantle-session" }, TestContext.Current.CancellationToken)); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.Single(factory.Clients);
    }
    /// <summary>短期 Key/Token 轮换不改变稳定绑定，实例与调度器变化后仍用原服务器/模型/主体。</summary>
    [Fact]
    public async Task RotatingCredentials_RecoverCompleteTargetAcrossClients()
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); var acquired = 0;
        source.Resolve = (principal, _) => ValueTask.FromResult(new AwsLlmSigningCredentials(principal, ++acquired == 1 ? "AKIDFIRST" : "AKIDSECOND", AwsCredentialSource.Secret, clock.Now.AddHours(1), acquired == 1 ? "token-first" : "token-second"));
        var calls = 0; using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("second.invalid", message.RequestUri!.Host); Assert.Contains("fixed-model-second", await message.Content!.ReadAsStringAsync(token));
            Assert.Contains(calls == 0 ? "AKIDFIRST" : "AKIDSECOND", Assert.Single(message.Headers.GetValues("Authorization"))); Assert.Equal(calls == 0 ? "token-first" : "token-second", Assert.Single(message.Headers.GetValues("x-amz-security-token")));
            var stream = calls++ != 0; return Reply(new MemoryStream(Bytes(stream ? Finished : "{\"id\":\"msg\"}")), stream);
        });
        var factory = new AwsHttpFactory(handler); var store = AwsFixture.Store(); var options = new AwsMantleLlmOptions { Region = "us-east-1" }; var targets = new[] { Target(id: "first", root: "https://first.invalid/proxy/anthropic/v1/", model: "fixed-model-first"), Target(id: "second", root: "https://second.invalid/proxy/anthropic/v1/", model: "fixed-model-second") };
        var original = new AwsMantleLlmProvider(factory, source, options, timeProvider: clock); var identity = original.BindingIdentity;
        await using (var first = Client(original, store, targets, 1)) await first.LLM.ClaudeMessages.CreateAsync(Request().Deserialize<ClaudeMessageRequest>()!, new() { SessionId = "mantle-session" }, TestContext.Current.CancellationToken);
        var recreated = new AwsMantleLlmProvider(factory, source, options, timeProvider: clock); await using var second = Client(recreated, store, targets, 0);
        await foreach (var item in second.LLM.ClaudeMessages.StreamAsync(Request().Deserialize<ClaudeMessageRequest>()!, new() { SessionId = "mantle-session" }, TestContext.Current.CancellationToken)) Assert.Equal("second", item.Metadata.TargetId);
        Assert.Equal(identity, recreated.BindingIdentity); Assert.Equal(2, calls); Assert.Equal(2, source.Calls); Assert.DoesNotContain("AKID", identity); Assert.DoesNotContain("token-", identity); Assert.DoesNotContain(AwsCredentialSource.Secret, JsonSerializer.Serialize(recreated));
    }
}
