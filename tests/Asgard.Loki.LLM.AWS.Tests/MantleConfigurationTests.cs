using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.AWS.Tests.MantleFixture;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>新的 Mantle 设置具有明确边界，不通过错误输入启用另一个 AWS API 或认证。</summary>
public sealed class MantleConfigurationTests
{
    /// <summary>认证/版本/Host 及注入值不能混入可签名的原生扩展设置。</summary>
    [Theory]
    [InlineData("Authorization", "secret")] [InlineData("Host", "host.invalid")] [InlineData("x-api-key", "secret")]
    [InlineData("x-amz-date", "date")] [InlineData("anthropic-version", "other")]
    [InlineData("anthropic-beta", "private-secret\r\ninjected")]
    public void InvalidHeaders_RejectBeforeAnyAcquisition(string header, string value)
    {
        var source = new AwsCredentialSource(new()); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new AwsHttpFactory(handler);
        var error = Assert.Throws<ArgumentException>(() => new AwsMantleLlmProvider(factory, source, new() { Region = "us-east-1", RequestHeaders = new Dictionary<string, string> { [header] = value } }));
        Assert.DoesNotContain("private-secret", error.Message); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
    /// <summary>有限策略与明确区域不能被无效配置绕过。</summary>
    [Fact]
    public void InvalidOptions_RejectWithoutSourceOrFactory()
    {
        var source = new AwsCredentialSource(new()); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new AwsHttpFactory(handler);
        foreach (var options in new[] { new AwsMantleLlmOptions { Region = "US-EAST-1" }, new() { Region = "us-east-1", AnthropicVersion = "bad\nversion" }, new() { Region = "us-east-1", MaximumBodyBytes = 0 }, new() { Region = "us-east-1", MaximumBufferedTerminalEvents = 0 }, new() { Region = "us-east-1", MaximumTrackedContentBlocks = 0 } })
            Assert.ThrowsAny<ArgumentException>(() => new AwsMantleLlmProvider(factory, source, options)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
    /// <summary>带用户信息、查询、片段或非 HTTP 目标在签名来源等待之前拒绝。</summary>
    [Theory]
    [InlineData("https://user:secret@host.invalid/anthropic/v1/")]
    [InlineData("https://host.invalid/anthropic/v1/?key=secret")]
    [InlineData("https://host.invalid/anthropic/v1/#fragment")]
    [InlineData("file:///tmp/model")]
    public async Task InvalidTarget_FailsBeforeAcquisition(string root)
    {
        var source = new AwsCredentialSource(new()); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new AwsHttpFactory(handler); var provider = new AwsMantleLlmProvider(factory, source, new() { Region = "us-east-1" });
        await Assert.ThrowsAsync<ArgumentException>(() => provider.CreateAsync(Request(), Context(Target(root: root)), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>唯一操作为 ClaudeMessages，注册不能把它冒充 Converse 或普通 Key 认证。</summary>
    [Fact]
    public void NativeSurfaceAndIdentity_AreSeparateFromRuntime()
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new AwsHttpFactory(handler); var mantle = new AwsMantleLlmProvider(factory, source, new() { Region = "us-east-1" });
        Assert.False((ILlmProvider)mantle is IBedrockConverseAdapter); Assert.False((ILlmProvider)mantle is IChatCompletionsAdapter); Assert.True(mantle.SupportsCredential(LlmCredentialKind.External)); Assert.False(mantle.SupportsCredential(LlmCredentialKind.ApiKey));
        Assert.Contains("aws-mantle-messages-sigv4-v1", mantle.BindingIdentity); Assert.NotEqual(new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }).BindingIdentity, mantle.BindingIdentity);
        Assert.Throws<ArgumentException>(() => Client(mantle, targets: [Target() with { Protocols = LlmProtocol.BedrockConverse }])); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
    /// <summary>Messages 不注入 InvokeModel 版本，也不默默删除调用者的原生扩展。</summary>
    [Fact]
    public async Task CallerNativeFields_AreNotStrippedOrMoved()
    {
        var clock = new AwsClock(); var input = Request(); input["anthropic_version"] = "caller-native-body-value"; input["anthropic_beta"] = new System.Text.Json.Nodes.JsonArray("caller-native-body-beta");
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!;
            Assert.Equal("caller-native-body-value", json["anthropic_version"]!.GetValue<string>()); Assert.Equal("caller-native-body-beta", json["anthropic_beta"]![0]!.GetValue<string>()); Assert.Equal("2023-06-01", Assert.Single(message.Headers.GetValues("anthropic-version")));
            return Reply(new MemoryStream(Bytes("{\"id\":\"msg\"}")), false);
        });
        await new AwsMantleLlmProvider(new AwsHttpFactory(handler), new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock).CreateAsync(input, Context(), TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>已选临时令牌覆盖默认旧值，但不修改借用客户端的原始默认配置。</summary>
    [Fact]
    public async Task SelectedTemporaryToken_OverridesStaleDefault()
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock) { Resolve = (principal, _) => ValueTask.FromResult(new AwsLlmSigningCredentials(principal, AwsCredentialSource.KeyId, AwsCredentialSource.Secret, clock.Now.AddHours(1), "selected-session")) };
        using var handler = new StubHttpHandler((message, _) => { Assert.Equal("selected-session", Assert.Single(message.Headers.GetValues("x-amz-security-token"))); return Task.FromResult(Reply(new MemoryStream(Bytes("{\"id\":\"msg\"}")), false)); });
        var factory = new AwsHttpFactory(handler) { DefaultHeader = "x-amz-security-token" }; await new AwsMantleLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock).CreateAsync(Request(), Context(), TestContext.Current.CancellationToken); Assert.Equal("stale", Assert.Single(Assert.Single(factory.Clients).DefaultRequestHeaders.GetValues("x-amz-security-token"))); Assert.Equal(1, handler.Calls);
    }
}
