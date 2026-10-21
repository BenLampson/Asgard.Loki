using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Anthropic.Tests.AnthropicHttpFixture;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>认证与资源配置属于完整 Session，重建实例不改选目标或凭据。</summary>
public sealed class NativeMessagesSessionTests
{
    /// <summary>版本和命名客户端允许的分隔字符不能碰撞，从而沿用另一套 HTTP 配置。</summary>
    [Fact]
    public async Task DelimiterCollision_CannotReuseChangedVersionAndClient()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes("{\"id\":\"msg\"}")), false))); var factory = new AnthropicHttpFactory(handler); var store = Store();
        await using (var first = Build(new AnthropicLlmProvider(factory, apiVersion: "2023:06", httpClientName: "client"), store))
            await first.LLM.ClaudeMessages.CreateAsync(new() { Model = "public", Messages = [], MaxTokens = 128 }, new() { SessionId = "session" }, TestContext.Current.CancellationToken);
        await using var second = Build(new AnthropicLlmProvider(factory, apiVersion: "2023", httpClientName: "06:client"), store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.ClaudeMessages.CreateAsync(new() { Model = "public", Messages = [], MaxTokens = 128 }, new() { SessionId = "session" }, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls); Assert.Single(factory.Clients);
    }

    /// <summary>真实旧身份缓存不能默默采用新的认证与终态策略，必须新 Session 或到期。</summary>
    [Fact]
    public async Task LegacySession_RejectsBeforeSecondHttp()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes("{\"id\":\"msg\"}")), false))); var factory = new AnthropicHttpFactory(handler); var store = Store();
        await using (var old = Build(new LegacyAnthropicProvider(new(factory)), store))
            await old.LLM.ClaudeMessages.CreateAsync(new() { Model = "public", Messages = [], MaxTokens = 128 }, new() { SessionId = "session" }, TestContext.Current.CancellationToken);
        await using var current = Build(new AnthropicLlmProvider(factory), store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => current.LLM.ClaudeMessages.CreateAsync(new() { Model = "public", Messages = [], MaxTokens = 128 }, new() { SessionId = "session" }, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls); Assert.Single(factory.Clients);
    }

    /// <summary>模式、Key、版本、客户端与资源策略变化均在第二次发包前拒绝旧 Session。</summary>
    [Theory]
    [InlineData("auth")] [InlineData("key")] [InlineData("version")] [InlineData("client")]
    [InlineData("bytes")] [InlineData("tail")] [InlineData("blocks")]
    public async Task ChangedBinding_FailsBeforeHttp(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes("{\"id\":\"msg\"}")), false)));
        var factory = new AnthropicHttpFactory(handler); var store = Store();
        await using (var first = Build(new AnthropicLlmProvider(factory), store))
            await first.LLM.ClaudeMessages.CreateAsync(new() { Model = "public", Messages = [], MaxTokens = 128 }, new() { SessionId = "session" }, TestContext.Current.CancellationToken);
        var options = new AnthropicHttpOptions { Authentication = change == "auth" ? AnthropicAuthentication.Bearer : AnthropicAuthentication.ApiKey,
            MaximumBodyBytes = change == "bytes" ? 1024 : 32 * 1024 * 1024, MaximumBufferedTerminalEvents = change == "tail" ? 2 : 128, MaximumTrackedContentBlocks = change == "blocks" ? 2 : 128 };
        var provider = new AnthropicLlmProvider(factory, options, apiVersion: change == "version" ? "2024-01-01" : "2023-06-01", httpClientName: change == "client" ? "other" : "Asgard.Loki.LLM.Anthropic");
        await using var second = Build(provider, store, key: change == "key" ? "rotated-key" : "test-key");
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.ClaudeMessages.CreateAsync(new() { Model = "public", Messages = [], MaxTokens = 128 }, new() { SessionId = "session" }, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls); Assert.Single(factory.Clients);
    }
    /// <summary>不同认证的同配置实例仍恢复相同服务器、实际模型和凭据。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SameBinding_RecoversCompleteTargetForJsonAndStream(bool bearer)
    {
        var calls = 0; using var handler = new StubHttpHandler((request, _) =>
        {
            Assert.Equal("bedrock-mantle.us-east-1.api.aws", request.RequestUri!.Host);
            if (bearer) Assert.Equal("test-key", request.Headers.Authorization!.Parameter); else Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-api-key")));
            return Task.FromResult(Reply(new MemoryStream(Bytes(calls++ == 0 ? "{\"id\":\"msg\"}" : Finished)), calls > 1));
        });
        var factory = new AnthropicHttpFactory(handler); var store = Store(); var options = new AnthropicHttpOptions { Authentication = bearer ? AnthropicAuthentication.Bearer : AnthropicAuthentication.ApiKey };
        var provider = new AnthropicLlmProvider(factory, options); var target = Target(provider, "https://bedrock-mantle.us-east-1.api.aws/anthropic/v1/", "explicit-model");
        await using (var first = Build(provider, store, target))
            await first.LLM.ClaudeMessages.CreateAsync(new() { Model = "public", Messages = [], MaxTokens = 128 }, new() { SessionId = "session" }, TestContext.Current.CancellationToken);
        await using var second = Build(new AnthropicLlmProvider(factory, options), store, target);
        await foreach (var item in second.LLM.ClaudeMessages.StreamAsync(new() { Model = "public", Messages = [], MaxTokens = 128 }, new() { SessionId = "session" }, TestContext.Current.CancellationToken)) Assert.Equal("explicit-model", item.Metadata.UpstreamModel);
        Assert.Equal(2, handler.Calls); Assert.Equal(2, factory.Clients.Count); Assert.All(factory.Clients, client => Assert.True(client.Disposed));
    }
}
