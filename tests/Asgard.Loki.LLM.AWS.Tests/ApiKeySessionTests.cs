using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>Bearer 仍绑定完整上游目标/Key 身份，跨客户端/模式复用不会变成自动 Key 轮换。</summary>
public sealed class ApiKeySessionTests
{
    /// <summary>JSON 与流式跨客户端恢复第二个完整目标，选择器变化不能重选服务器/模型。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task CrossModeRecovery_RetainsWholeTarget(bool converse, bool firstStream)
    {
        var store = AwsFixture.Store(); var second = ApiKeyFixture.Target(converse, "second") with { UpstreamModel = "vendor.actual-model:0" }; using var handler = new StubHttpHandler((request, _) => { Assert.Equal("second.invalid", request.RequestUri!.Host); Assert.Contains("vendor.actual-model%3A0/", request.RequestUri.AbsoluteUri); Assert.Equal(ApiKeyFixture.Key, request.Headers.Authorization!.Parameter); var stream = request.RequestUri.AbsolutePath.EndsWith("stream", StringComparison.Ordinal); return Task.FromResult(ApiKeyFixture.Reply(stream, new OllamaBodyStream(ApiKeyFixture.Bytes(converse, stream)))); }); var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, new() { Region = "us-east-1" }); var targets = new[] { ApiKeyFixture.Target(converse), second };
        async Task Invoke(Asgard.Loki.Abstractions.ILokiClient client, bool stream) { if (stream) { await foreach (var _ in ApiKeyFixture.PublicStream(client, converse, TestContext.Current.CancellationToken)) { } } else await ApiKeyFixture.PublicComplete(client, converse, TestContext.Current.CancellationToken); }
        await using (var first = ApiKeyFixture.Client(provider, converse, store, targets, 1)) await Invoke(first, firstStream); var before = await store.GetAsync("key-session", TestContext.Current.CancellationToken); await using (var next = ApiKeyFixture.Client(provider, converse, store, targets)) await Invoke(next, !firstStream); var after = await store.GetAsync("key-session", TestContext.Current.CancellationToken); Assert.Equal(before!.Fingerprint, after!.Fingerprint); Assert.Equal(2, handler.Calls); Assert.All(factory.Clients, client => Assert.True(client.Disposed)); Assert.False(factory.Disposed);
    }
    /// <summary>普通 API Key 改变不能伪装成已验证主体下的短期令牌刷新。</summary>
    [Fact]
    public async Task ChangedKey_RejectsPreviousSessionBeforeHttp()
    {
        var store = AwsFixture.Store(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(ConverseFixture.Reply())); var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, new() { Region = "us-east-1" }); await using (var first = ApiKeyFixture.Client(provider, true, store)) await ApiKeyFixture.PublicComplete(first, true, TestContext.Current.CancellationToken); await using var next = ApiKeyFixture.Client(provider, true, store, credential: new("identity", "different-fixture-key")); await Assert.ThrowsAsync<InvalidOperationException>(() => ApiKeyFixture.PublicComplete(next, true, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls); Assert.Single(factory.Clients);
    }
    /// <summary>区域、字节策略或命名客户端变化仍进入供应商配置绑定，不静默迁移旧会话。</summary>
    [Theory] [InlineData("region")] [InlineData("bytes")] [InlineData("client")]
    public async Task ChangedConfiguration_InvalidatesPreviousBinding(string field)
    {
        var store = AwsFixture.Store(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(ConverseFixture.Reply())); var factory = new AwsHttpFactory(handler); var options = new AwsLlmOptions { Region = "us-east-1" }; await using (var first = ApiKeyFixture.Client(new(factory, options), true, store)) await ApiKeyFixture.PublicComplete(first, true, TestContext.Current.CancellationToken); var changed = field == "region" ? options with { Region = "us-west-2" } : field == "bytes" ? options with { MaximumBodyBytes = 1024 } : options; await using var next = ApiKeyFixture.Client(new(factory, changed, httpClientName: field == "client" ? "different-client" : "Asgard.Loki.LLM.AWS"), true, store); await Assert.ThrowsAsync<InvalidOperationException>(() => ApiKeyFixture.PublicComplete(next, true, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>External/SigV4 与 API Key 是不同完整绑定，不按共同 provider ID 继承会话。</summary>
    [Fact]
    public async Task ChangedAuthenticationMode_DoesNotInheritSignedSession()
    {
        var store = AwsFixture.Store(); var source = new AwsCredentialSource(new()); using var handler = new StubHttpHandler((_, _) => Task.FromResult(ConverseFixture.Reply())); var factory = new AwsHttpFactory(handler); await using (var signed = ConverseFixture.Client(new(factory, source, new() { Region = "us-east-1" }, timeProvider: new AwsClock()), store)) await ApiKeyFixture.PublicComplete(signed, true, TestContext.Current.CancellationToken); await using var key = ApiKeyFixture.Client(new(factory, new() { Region = "us-east-1" }), true, store); await Assert.ThrowsAsync<InvalidOperationException>(() => ApiKeyFixture.PublicComplete(key, true, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls);
    }
}
