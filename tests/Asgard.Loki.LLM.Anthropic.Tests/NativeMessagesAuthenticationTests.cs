using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Anthropic.Tests.AnthropicHttpFixture;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>显式选择唯一认证身份，跨模式默认头不能借用或泄漏。</summary>
public sealed class NativeMessagesAuthenticationTests
{
    /// <summary>另一认证或 AWS 签名默认头在发送前失败，不修改借用的默认配置。</summary>
    [Theory]
    [InlineData(false, "Authorization")] [InlineData(true, "x-api-key")]
    [InlineData(false, "x-goog-api-key")] [InlineData(true, "api-key")]
    [InlineData(false, "x-amz-date")] [InlineData(true, "x-amz-security-token")]
    [InlineData(false, "x-amz-content-sha256")] [InlineData(true, "x-amz-content-sha256")]
    public async Task UnselectedDefaultAuthentication_FailsBeforeSend(bool bearer, string header)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send"));
        var factory = new AnthropicHttpFactory(handler) { Defaults = client => client.DefaultRequestHeaders.Add(header, header == "Authorization" ? "Bearer stale" : "stale") };
        var provider = new AnthropicLlmProvider(factory, new AnthropicHttpOptions { Authentication = bearer ? AnthropicAuthentication.Bearer : AnthropicAuthentication.ApiKey });
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(new(), Context(provider), TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls); var borrowedDefaults = Assert.Single(factory.Clients); Assert.Equal(header == "Authorization" ? "Bearer stale" : "stale", Assert.Single(borrowedDefaults.DefaultRequestHeaders.GetValues(header))); Assert.True(borrowedDefaults.Disposed);
    }
    /// <summary>API Key 模式和 Bearer 都只接受显式 Key 类型，不借用匿名或外部身份。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task NonApiKeyCredential_FailsBeforeFactory(bool bearer, bool external)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new AnthropicHttpFactory(handler);
        var provider = new AnthropicLlmProvider(factory, new AnthropicHttpOptions { Authentication = bearer ? AnthropicAuthentication.Bearer : AnthropicAuthentication.ApiKey });
        var credential = external ? LlmCredential.External("key", "principal") : LlmCredential.Anonymous("key"); var context = new LlmAdapterContext(Target(provider), credential, "public", "session");
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(new(), context, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect()); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
        async Task Collect() { await foreach (var _ in provider.StreamAsync(new(), context, TestContext.Current.CancellationToken)) { } }
    }
    /// <summary>目标根地址不能附带另一认证、查询或片段，预先失败不触碰工厂。</summary>
    [Theory]
    [InlineData("https://user:secret@native.invalid/v1/")]
    [InlineData("https://native.invalid/v1/?key=secret")]
    [InlineData("https://native.invalid/v1/#fragment")]
    [InlineData("file:///tmp/model")]
    public async Task InvalidRoot_FailsBeforeFactory(string root)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new AnthropicHttpFactory(handler); var provider = new AnthropicLlmProvider(factory);
        var context = new LlmAdapterContext(Target(provider, root), new("key", "test-key"), "public", "session");
        await Assert.ThrowsAsync<ArgumentException>(() => provider.CreateAsync(new(), context, TestContext.Current.CancellationToken)); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>工厂回调不能改变已准备的直接适配器正文，取消也不会创建客户端。</summary>
    [Fact]
    public async Task Request_IsSnapshottedBeforeFactoryCallback()
    {
        var payload = new JsonObject { ["future"] = new JsonObject { ["n"] = 0 } };
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var actual = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)); Assert.Equal(0, actual!["future"]!["n"]!.GetValue<int>());
            return Reply(new MemoryStream(Bytes("{\"id\":\"msg\"}")), false);
        });
        var factory = new AnthropicHttpFactory(handler) { Defaults = _ => payload["future"]!["n"] = 9 }; var provider = new AnthropicLlmProvider(factory);
        await provider.CreateAsync(payload, Context(provider), TestContext.Current.CancellationToken); Assert.Equal(9, payload["future"]!["n"]!.GetValue<int>()); Assert.Equal(1, handler.Calls);
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CreateAsync(payload, Context(provider), cancel.Token)); Assert.Single(factory.Clients);
    }
    /// <summary>保留原二进制构造签名，新 HTTP 域显式区分认证与资源策略。</summary>
    [Fact]
    public void PolicyAndConstructor_AreExplicitAndSecretFree()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new AnthropicHttpFactory(handler);
        var legacy = typeof(AnthropicLlmProvider).GetConstructor([typeof(IHttpClientFactory), typeof(string), typeof(string), typeof(string)]); Assert.NotNull(legacy);
        var standard = new AnthropicLlmProvider(factory); Assert.Contains("anthropic-http-v2", standard.BindingIdentity); Assert.DoesNotContain("test-key", standard.BindingIdentity);
        Assert.Equal(standard.BindingIdentity, new AnthropicLlmProvider(factory, new AnthropicHttpOptions()).BindingIdentity);
        Assert.NotEqual(standard.BindingIdentity, new AnthropicLlmProvider(factory, new AnthropicHttpOptions { Authentication = AnthropicAuthentication.Bearer }).BindingIdentity);
        foreach (var invalid in new[] { new AnthropicHttpOptions { Authentication = (AnthropicAuthentication)9 }, new() { MaximumBodyBytes = 0 }, new() { MaximumBufferedTerminalEvents = 0 }, new() { MaximumTrackedContentBlocks = 0 } })
            Assert.Throws<ArgumentOutOfRangeException>(() => new AnthropicLlmProvider(factory, invalid));
        Assert.Throws<ArgumentException>(() => new AnthropicLlmProvider(factory, apiVersion: "bad\nversion")); Assert.Empty(factory.Clients);
    }
}
