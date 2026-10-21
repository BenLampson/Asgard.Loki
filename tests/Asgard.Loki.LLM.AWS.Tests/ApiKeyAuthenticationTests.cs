using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>构造入口决定认证种类，不回退签名、不解析复合 Key，也不借用工厂默认凭据。</summary>
public sealed class ApiKeyAuthenticationTests
{
    /// <summary>同一供应商类型的两个明确入口有独立绑定域，旧默认 SigV4 字节由既有回归继续核验。</summary>
    [Fact]
    public void ExplicitConstructor_SelectsOnlyOneCredentialKind()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new AwsHttpFactory(handler); var source = new AwsCredentialSource(new()); var key = new AwsLlmProvider(factory, new() { Region = "us-east-1" }); var signed = new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }); Assert.True(key.SupportsCredential(LlmCredentialKind.ApiKey)); Assert.False(key.SupportsCredential(LlmCredentialKind.External)); Assert.False(key.SupportsCredential(LlmCredentialKind.Anonymous)); Assert.False(signed.SupportsCredential(LlmCredentialKind.ApiKey)); Assert.True(signed.SupportsCredential(LlmCredentialKind.External)); Assert.Contains(":aws-bedrock-api-key-v1:", key.BindingIdentity); Assert.Contains(":aws-claude-eventstream-v1:", signed.BindingIdentity); Assert.NotEqual(key.BindingIdentity, signed.BindingIdentity); Assert.DoesNotContain(ApiKeyFixture.Key, JsonSerializer.Serialize(key)); Assert.Equal(0, source.Calls);
    }
    /// <summary>不支持的 External/Anonymous 凭据在四个方法中均不能创建客户端或退回其他认证。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task WrongCredentialKind_FailsBeforeFactory(bool converse, bool stream)
    {
        foreach (var credential in new[] { LlmCredential.External("identity", "principal-a"), LlmCredential.Anonymous("identity") }) { using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, new() { Region = "us-east-1" }); await Assert.ThrowsAsync<ArgumentException>(() => ApiKeyFixture.Invoke(provider, converse, stream, TestContext.Current.CancellationToken, context: ApiKeyFixture.Context(converse, credential: credential))); Assert.Empty(factory.Clients); }
    }
    /// <summary>公共注册同样拒绝错误认证种类，不能等到首次请求才借用默认头。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void PublicRegistration_RejectsUnsupportedCredential(bool external)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new AwsHttpFactory(handler); Assert.ThrowsAny<ArgumentException>(() => ApiKeyFixture.Client(new(factory, new() { Region = "us-east-1" }), true, credential: external ? LlmCredential.External("identity", "principal-a") : LlmCredential.Anonymous("identity"))); Assert.Empty(factory.Clients);
    }
    /// <summary>Bearer 不借用其他 Key、AWS 日期/摘要/令牌或未选择原生控制头，四个操作均适用。</summary>
    [Theory] [InlineData("api-key")] [InlineData("x-api-key")] [InlineData("x-goog-api-key")] [InlineData("x-amz-date")] [InlineData("x-amz-content-sha256")] [InlineData("x-amz-security-token")] [InlineData("x-amzn-bedrock-trace")]
    public async Task UnselectedDefaults_FailBeforeHttp(string header)
    {
        foreach (var converse in new[] { false, true }) foreach (var stream in new[] { false, true }) { using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new AwsHttpFactory(handler) { DefaultHeader = header }; await Assert.ThrowsAsync<InvalidOperationException>(() => ApiKeyFixture.Invoke(new(factory, new() { Region = "us-east-1" }), converse, stream, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); }
    }
    /// <summary>实际目标 Host 覆盖工厂默认路由，区域/模型不从 Key 里拆分或改写。</summary>
    [Theory] [InlineData("https://first.invalid:8443/proxy/", "first.invalid:8443")] [InlineData("http://[::1]:1234/proxy/", "[::1]:1234")] [InlineData("https://bücher.invalid/proxy/", "xn--bcher-kva.invalid")]
    public async Task SelectedHostAndOpaqueKey_AreExact(string root, string expectedHost)
    {
        const string opaque = "fixture-key|caller-data"; using var handler = new StubHttpHandler((request, _) => { Assert.Equal(expectedHost, request.Headers.Host); Assert.Equal(opaque, request.Headers.Authorization!.Parameter); Assert.Equal("Bearer", request.Headers.Authorization.Scheme); Assert.Equal(new Uri(root).Host, request.RequestUri!.Host); return Task.FromResult(ConverseFixture.Reply()); }); var provider = new AwsLlmProvider(new AwsHttpFactory(handler) { DefaultHeader = "Host" }, new() { Region = "us-east-1" }); var target = ConverseFixture.Target() with { BaseAddress = new(root) }; await ApiKeyFixture.Complete(provider, true, context: ApiKeyFixture.Context(true, target, new("identity", opaque)), token: TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>非法头值在工厂/HTTP 前失败，不剪裁、转码或回显 Key。</summary>
    [Theory] [InlineData("fixture key")] [InlineData("fixture\r\nkey")] [InlineData("fixture\tkey")] [InlineData("fixture中key")] [InlineData("fixture\0key")] [InlineData("fixture\u007fkey")]
    public async Task InvalidHeaderValue_FailsWithoutLeakingKey(string key)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, new() { Region = "us-east-1" }); var error = await Assert.ThrowsAsync<ArgumentException>(() => ApiKeyFixture.Complete(provider, true, context: ApiKeyFixture.Context(true, credential: new("identity", key)), token: TestContext.Current.CancellationToken)); Assert.DoesNotContain(key, error.Message); Assert.Empty(factory.Clients);
    }
    /// <summary>凭据及缓存默认展示不包含 Key，完整目标缓存只保存既有安全指纹。</summary>
    [Fact]
    public async Task CredentialAndSession_DoNotSerializeSecrets()
    {
        var store = AwsFixture.Store(); var credential = new LlmCredential("identity", ApiKeyFixture.Key); using var handler = new StubHttpHandler((_, _) => Task.FromResult(ConverseFixture.Reply())); var factory = new AwsHttpFactory(handler); await using (var client = ApiKeyFixture.Client(new(factory, new() { Region = "us-east-1" }), true, store, credential: credential)) await ApiKeyFixture.PublicComplete(client, true, TestContext.Current.CancellationToken); var session = await store.GetAsync("key-session", TestContext.Current.CancellationToken); Assert.NotNull(session); Assert.DoesNotContain(ApiKeyFixture.Key, JsonSerializer.Serialize(session)); Assert.DoesNotContain(ApiKeyFixture.Key, JsonSerializer.Serialize(credential)); Assert.Equal("identity", credential.ToString()); Assert.False(factory.Disposed);
    }
    /// <summary>预取消不创建客户端，也不读取任何签名来源。</summary>
    [Theory] [MemberData(nameof(ApiKeyFixture.Operations), MemberType = typeof(ApiKeyFixture))]
    public async Task PreCancelled_AcquiresNoClient(bool converse, bool stream)
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new AwsHttpFactory(handler); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ApiKeyFixture.Invoke(new(factory, new() { Region = "us-east-1" }), converse, stream, cancel.Token)); Assert.Empty(factory.Clients);
    }
}
