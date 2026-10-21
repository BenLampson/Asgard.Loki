using System.Text;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>所有已选端点、协议、版本与默认头身份都显式验证。</summary>
public sealed class ConfigurationTests
{
    /// <summary>普通 Key/匿名不能隐式解释为 AWS 签名凭据。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnsupportedCredential_IsRejected(bool anonymous)
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(AwsFixture.Reply())); var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }); var credential = anonymous ? LlmCredential.Anonymous("identity") : new("identity", "fixture-key"); Assert.False(provider.SupportsCredential((LlmCredentialKind)999)); Assert.Throws<ArgumentException>(() => AwsFixture.Client(provider, credential: credential)); await Assert.ThrowsAsync<ArgumentException>(() => provider.CreateAsync(AwsFixture.Request(), AwsFixture.Context(credential: credential), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
    /// <summary>根地址不接受隐藏认证、查询、片段或非 HTTP(S) 协议。</summary>
    [Theory] [InlineData("relative")] [InlineData("ftp://host.invalid/")] [InlineData("https://user:secret@host.invalid/")] [InlineData("https://host.invalid/?key=secret")] [InlineData("https://host.invalid/#fragment")]
    public async Task InvalidRoot_FailsBeforeSource(string root)
    {
        var source = new AwsCredentialSource(new()); using var handler = new StubHttpHandler((_, _) => Task.FromResult(AwsFixture.Reply())); var factory = new AwsHttpFactory(handler); await Assert.ThrowsAsync<ArgumentException>(() => new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }).CreateAsync(AwsFixture.Request(), AwsFixture.Context(AwsFixture.Target() with { BaseAddress = new(root, UriKind.RelativeOrAbsolute) }), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
    /// <summary>模型相对路径不能导致 URI 合并丢失模型路由。</summary>
    [Theory] [InlineData(".")] [InlineData("..")]
    public async Task RelativeModel_FailsBeforeSource(string model)
    {
        var source = new AwsCredentialSource(new()); using var handler = new StubHttpHandler((_, _) => Task.FromResult(AwsFixture.Reply())); var factory = new AwsHttpFactory(handler); await Assert.ThrowsAsync<ArgumentException>(() => new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }).CreateAsync(AwsFixture.Request(), AwsFixture.Context(AwsFixture.Target() with { UpstreamModel = model }), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
    /// <summary>正文版本冲突或类型错误在签名来源等待之前失败。</summary>
    [Theory] [InlineData("null")] [InlineData("42")] [InlineData("\"other\"")]
    public async Task ConflictingVersion_FailsBeforeSource(string version)
    {
        var source = new AwsCredentialSource(new()); using var handler = new StubHttpHandler((_, _) => Task.FromResult(AwsFixture.Reply())); var factory = new AwsHttpFactory(handler); var input = AwsFixture.Request(); input["anthropic_version"] = System.Text.Json.Nodes.JsonNode.Parse(version); await Assert.ThrowsAsync<ArgumentException>(() => new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }).CreateAsync(input, AwsFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
    /// <summary>未选择的 Key/Session Token/原生配置不能从借用工厂混入已签名请求。</summary>
    [Theory] [InlineData("Authorization", false)] [InlineData("x-amz-date", false)] [InlineData("x-amz-content-sha256", false)] [InlineData("x-amz-security-token", false)] [InlineData("x-amz-security-token", true)] [InlineData("x-amz-other", false)] [InlineData("api-key", false)] [InlineData("x-api-key", false)] [InlineData("x-goog-api-key", false)] [InlineData("x-amzn-bedrock-trace", false)] [InlineData("x-amzn-bedrock-trace", true)]
    public async Task DefaultHeaders_CannotAlterSelectedIdentity(string header, bool selected)
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); if (selected && header == "x-amz-security-token") source.Resolve = (principal, _) => ValueTask.FromResult(new AwsLlmSigningCredentials(principal, AwsCredentialSource.KeyId, AwsCredentialSource.Secret, clock.Now.AddHours(1), "selected-token")); using var handler = new StubHttpHandler((request, _) => { Assert.StartsWith("AWS4-HMAC-SHA256 ", Assert.Single(request.Headers.GetValues("Authorization"))); if (selected) Assert.Equal(header == "x-amz-security-token" ? "selected-token" : "ENABLED", Assert.Single(request.Headers.GetValues(header))); return Task.FromResult(AwsFixture.Reply()); }); var factory = new AwsHttpFactory(handler) { DefaultHeader = header }; var options = new AwsLlmOptions { Region = "us-east-1", RequestHeaders = selected && header == "x-amzn-bedrock-trace" ? new Dictionary<string, string> { [header] = "ENABLED" } : new Dictionary<string, string>() }; var provider = new AwsLlmProvider(factory, source, options, timeProvider: clock); var success = selected || header is "Authorization" or "x-amz-date" or "x-amz-content-sha256";
        if (success) await provider.CreateAsync(AwsFixture.Request(), AwsFixture.Context(), TestContext.Current.CancellationToken); else await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(AwsFixture.Request(), AwsFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(success ? 1 : 0, handler.Calls); Assert.Equal(1, source.Calls); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>区域/策略/原生头的非法配置不能延迟到实际请求。</summary>
    [Theory] [InlineData("region")] [InlineData("zero")] [InlineData("version")] [InlineData("header-name")] [InlineData("header-value")] [InlineData("header-duplicate")]
    public void InvalidOptions_AreRejected(string scenario)
    {
        var headers = new Dictionary<string, string>(); if (scenario == "header-name") headers["Authorization"] = "other"; if (scenario == "header-value") headers["x-amzn-bedrock-trace"] = "secret\r\ninjected"; if (scenario == "header-duplicate") { headers["X-Amzn-Bedrock-Trace"] = "ENABLED"; headers["x-amzn-bedrock-trace"] = "DISABLED"; } var options = new AwsLlmOptions { Region = scenario == "region" ? "us-east-1/other" : "us-east-1", ClaudeAnthropicVersion = scenario == "version" ? "" : "bedrock-2023-05-31", MaximumBodyBytes = scenario == "zero" ? 0 : 1024, RequestHeaders = headers }; using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); Assert.ThrowsAny<ArgumentException>(() => new AwsLlmProvider(new AwsHttpFactory(handler), new AwsCredentialSource(new()), options));
    }
    /// <summary>原始 Unicode 与 caller-owned 选项集合快照独立验证。</summary>
    [Fact]
    public async Task InputUnicodeAndHeaderSnapshot_AreValidated()
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler((request, _) => { Assert.Equal("ENABLED", Assert.Single(request.Headers.GetValues("x-amzn-bedrock-trace"))); return Task.FromResult(AwsFixture.Reply()); }); var headers = new Dictionary<string, string> { ["x-amzn-bedrock-trace"] = "ENABLED" }; var provider = new AwsLlmProvider(new AwsHttpFactory(handler), source, new() { Region = "us-east-1", RequestHeaders = headers }, timeProvider: clock); var identity = provider.BindingIdentity; headers["x-amzn-bedrock-trace"] = "DISABLED"; Assert.Equal(identity, provider.BindingIdentity); var input = AwsFixture.Request(); input["future"] = new string((char)0xd800, 1); await Assert.ThrowsAsync<EncoderFallbackException>(() => provider.CreateAsync(input, AwsFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); await provider.CreateAsync(AwsFixture.Request(), AwsFixture.Context(), TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
}
