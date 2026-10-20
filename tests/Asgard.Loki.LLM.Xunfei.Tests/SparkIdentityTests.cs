using System.Text.Json;
using Xunit;

namespace Asgard.Loki.LLM.Xunfei.Tests;

/// <summary>完整 Session 固定 AppId/域名/模型/主体/实现，凭据轮换与配置漂移分开验证。</summary>
public sealed class SparkIdentityTests
{
    /// <summary>稳定主体下 Key 轮换只改变本次握手签名，不改变原生完整 Session 绑定。</summary>
    [Fact]
    public async Task SamePrincipalCanRotateKeysWithSeparateOwnedConnections()
    {
        using var source = new SparkSource(); using var factory = new SparkSocketFactory(); var provider = new XunfeiSparkProvider(source, SparkFixture.Policy(), factory, timeProvider: new SparkClock()); var identity = provider.BindingIdentity; var store = SparkFixture.Store(); await using var client = SparkFixture.Build(provider, store); var call = new LlmCallOptions { SessionId = "bound" };
        await client.LLM.Spark.CompleteAsync(SparkFixture.Request(), call, TestContext.Current.CancellationToken); var first = factory.Socket; var firstUri = factory.Address; source.Credentials = new("fixture-spark-principal", "rotated-key", "rotated-secret", DateTimeOffset.MaxValue); factory.Socket = SparkSocket.Native(); await client.LLM.Spark.CompleteAsync(SparkFixture.Request(), call, TestContext.Current.CancellationToken);
        Assert.Equal(identity, provider.BindingIdentity); Assert.NotEqual(firstUri, factory.Address); Assert.Equal(1, first.Disposals); Assert.Equal(1, factory.Socket.Disposals); Assert.Equal(2, source.Calls); Assert.Equal(2, factory.Calls); Assert.Equal("native-target", (await store.GetAsync("bound", TestContext.Current.CancellationToken))!.TargetId);
    }
    /// <summary>AppId、来源/工厂和传输限额配置改变后，旧 Session 在取凭据和建立连接之前失败。</summary>
    [Theory] [InlineData("appid")] [InlineData("source")] [InlineData("factory")] [InlineData("message")] [InlineData("events")] [InlineData("close-timeout")]
    public async Task ChangedProviderConfigurationCannotReuseBoundSession(string kind)
    {
        using var source = new SparkSource(); using var factory = new SparkSocketFactory(); var clock = new SparkClock(); var original = new XunfeiSparkProvider(source, SparkFixture.Policy(), factory, timeProvider: clock); var store = SparkFixture.Store(); await using (var first = SparkFixture.Build(original, store)) await first.LLM.Spark.CompleteAsync(SparkFixture.Request(), new() { SessionId = "bound" }, TestContext.Current.CancellationToken);
        if (kind == "source") source.BindingIdentity = "changed-source"; if (kind == "factory") factory.BindingIdentity = "changed-factory"; var policy = SparkFixture.Policy() with { AppId = kind == "appid" ? "other-app" : "fixture-app-123", MaximumMessageBytes = kind == "message" ? 4096 : 1024 * 1024, MaximumEvents = kind == "events" ? 128 : 8192, CloseTimeout = TimeSpan.FromSeconds(kind == "close-timeout" ? 20 : 10) }; var changed = new XunfeiSparkProvider(source, policy, factory, timeProvider: clock); Assert.NotEqual(original.BindingIdentity, changed.BindingIdentity);
        await using var second = SparkFixture.Build(changed, store); await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.Spark.CompleteAsync(SparkFixture.Request(), new() { SessionId = "bound" }, TestContext.Current.CancellationToken)); Assert.Equal(1, source.Calls); Assert.Equal(1, factory.Calls);
    }
    /// <summary>过期/错误主体/来源失败在连接前拒绝，不刷新或失效转移。</summary>
    [Theory] [InlineData("expired")] [InlineData("principal")] [InlineData("source")] [InlineData("source-await")] [InlineData("factory")] [InlineData("pipe-key")]
    public async Task InvalidAuthenticationDoesNotConnect(string kind)
    {
        using var source = new SparkSource(); using var factory = new SparkSocketFactory(); var clock = new SparkClock(); var provider = new XunfeiSparkProvider(source, SparkFixture.Policy(), factory, timeProvider: clock); var context = SparkFixture.Context();
        if (kind == "expired") source.Credentials = new("fixture-spark-principal", "key", "secret", clock.Now); if (kind == "principal") source.Credentials = new("wrong-principal", "key", "secret", DateTimeOffset.MaxValue); if (kind == "source") source.BindingIdentity = "changed"; if (kind == "factory") factory.BindingIdentity = "changed"; if (kind == "source-await") source.Acquire = _ => { source.BindingIdentity = "changed"; return ValueTask.FromResult(source.Credentials); }; if (kind == "pipe-key") context = new(context.Target, new("spark-identity", "app|secret|key"), "public-spark", "session");
        Assert.NotNull(await Record.ExceptionAsync(() => SparkFixture.ReadAsync(provider, TestContext.Current.CancellationToken, context: context))); Assert.Equal(0, factory.Calls); Assert.InRange(source.Calls, 0, 1);
    }
    /// <summary>无效路径或认证 URL 覆盖在签名/认证之前失败，不擅自增加版本或选择模型。</summary>
    [Theory] [InlineData("https://host.example.invalid/path?auth=bad")] [InlineData("https://user:pass@host.example.invalid/path")] [InlineData("https://host.example.invalid/path#fragment")] [InlineData("https://host.example.invalid/escaped%20path")]
    public async Task EndpointMustBeExplicitUnambiguousNativeAddress(string endpoint)
    {
        using var source = new SparkSource(); using var factory = new SparkSocketFactory(); await Assert.ThrowsAsync<ArgumentException>(() => SparkFixture.ReadAsync(new(source, SparkFixture.Policy(), factory), TestContext.Current.CancellationToken, context: SparkFixture.Context(endpoint))); Assert.Equal(0, source.Calls); Assert.Equal(0, factory.Calls);
    }
    /// <summary>无效选项不能建立可用供应商；声明只接受原生 Spark/External。</summary>
    [Theory] [InlineData("appid")] [InlineData("request")] [InlineData("message")] [InlineData("total")] [InlineData("events")] [InlineData("fragments")] [InlineData("tail")] [InlineData("connect")] [InlineData("close")]
    public void InvalidPoliciesAreRejectedAtConstruction(string kind)
    {
        using var source = new SparkSource(); using var factory = new SparkSocketFactory(); var policy = SparkFixture.Policy() with { AppId = kind == "appid" ? "bad app" : "fixture-app-123", MaximumRequestBytes = kind == "request" ? 0 : 1024, MaximumMessageBytes = kind == "message" ? 0 : 1024, MaximumResponseBytes = kind == "total" ? 0 : 8192, MaximumEvents = kind == "events" ? 0 : 128, MaximumFragmentsPerMessage = kind == "fragments" ? 0 : 128, MaximumBufferedTerminalEvents = kind == "tail" ? 0 : 128, ConnectTimeout = TimeSpan.FromSeconds(kind == "connect" ? 0 : 30), CloseTimeout = TimeSpan.FromSeconds(kind == "close" ? 0 : 10) };
        Assert.ThrowsAny<ArgumentException>(() => new XunfeiSparkProvider(source, policy, factory)); Assert.Equal(0, factory.Calls);
    }
}
