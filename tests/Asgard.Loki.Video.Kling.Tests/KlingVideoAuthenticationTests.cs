using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.Kling.Tests.KlingFixture;

namespace Asgard.Loki.Video.Kling.Tests;

/// <summary>原生 API Key/JWT 的独立向量、时间窗口、主体配置和借用资源验证。</summary>
public sealed class KlingVideoAuthenticationTests
{
    /// <summary>JWT 必须逐字匹配独立 HS256 向量，不加 iat、填充或其他自定义字段。</summary>
    [Fact]
    public async Task Jwt_MatchesIndependentVector()
    {
        using var handler = new StubHttpHandler((request, _) =>
        {
            var actual = request.Headers.Authorization!; Assert.Equal("Bearer", actual.Scheme); Assert.Equal(Data().GetProperty("jwt_vector").GetProperty("token").GetString(), actual.Parameter); Assert.DoesNotContain("=", actual.Parameter!);
            return Task.FromResult(Json(Query(0)));
        });
        var adapter = new KlingVideoAdapter(new StubHttpClientFactory(handler), Options(), new KlingTestClock()); await adapter.GetAsync("kling-fixture-01", TestContext.Current.CancellationToken);
    }
    /// <summary>后续显式调用重新签发令牌，实际稳定主体与完整任务绑定不随时间变化。</summary>
    [Fact]
    public async Task JwtRotation_DoesNotChangeTaskBinding()
    {
        var clock = new KlingTestClock(); var tokens = new List<string>(); using var handler = new StubHttpHandler((request, _) =>
        { tokens.Add(request.Headers.Authorization!.Parameter!); return Task.FromResult(Json(tokens.Count == 1 ? Data().GetProperty("submit_response") : Query(4))); });
        var factory = new StubHttpClientFactory(handler); await using var original = Build(factory, clock: clock); var task = await original.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        clock.Now = clock.Now.AddHours(2); await using var rebuilt = Build(factory, clock: clock); var current = await rebuilt.Video.GetAsync(task.Reference, TestContext.Current.CancellationToken);
        Assert.Equal(VideoTaskState.Succeeded, current.Snapshot.State); Assert.NotEqual(tokens[0], tokens[1]); Assert.Equal(2, handler.Calls);
    }
    /// <summary>客户端创建期间 JWT 过期或时钟回退到生效时间前，不发送请求也不重签。</summary>
    [Theory] [InlineData(1800)] [InlineData(-6)]
    public async Task JwtOutsideSendWindow_FailsBeforeHttp(int seconds)
    {
        var clock = new KlingTestClock(); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new KlingTestHttpFactory(handler) { OnCreate = () => clock.Now = clock.Now.AddSeconds(seconds) };
        var adapter = new KlingVideoAdapter(factory, Options(), clock); await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.GetAsync("task", TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls); Assert.Equal(1, factory.ClientsDisposed);
    }
    /// <summary>API Key 保持原始文本，不按前缀当作网关 Key 或改为 JWT。</summary>
    [Theory] [InlineData("sk-fictional-native-key")] [InlineData("opaque-native-key")]
    public async Task ApiKey_IsExplicitAndUnmodified(string key)
    {
        using var handler = new StubHttpHandler((request, _) => { Assert.Equal("Bearer " + key, request.Headers.Authorization!.ToString()); Assert.Equal("https://kling.invalid/v1/videos/text2video/task", request.RequestUri!.AbsoluteUri); return Task.FromResult(Json("{\"code\":0,\"data\":{\"task_id\":\"task\",\"task_status\":\"processing\"}}")); });
        var adapter = new KlingVideoAdapter(new StubHttpClientFactory(handler), Options(authentication: KlingVideoAuthentication.ApiKey, apiKey: key)); await adapter.GetAsync("task", TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>两种凭据均不进入配置 JSON、显示或目标摘要。</summary>
    [Theory] [InlineData(KlingVideoAuthentication.ApiKey)] [InlineData(KlingVideoAuthentication.AccessKeyJwt)]
    public void Secrets_StayOutOfMetadata(KlingVideoAuthentication authentication)
    {
        var options = Options(authentication: authentication); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var adapter = new KlingVideoAdapter(new StubHttpClientFactory(handler), options);
        foreach (var secret in new[] { options.ApiKey, options.AccessKeyId, options.SecretAccessKey }.Where(value => value.Length > 0))
        { Assert.DoesNotContain(secret, JsonSerializer.Serialize(options)); Assert.DoesNotContain(secret, adapter.BindingIdentity); Assert.DoesNotContain(secret, options.ToString()!); }
    }
    /// <summary>SDK 不接管工厂和时钟；每次调用创建的 HTTP 客户端在结果返回前释放。</summary>
    [Fact]
    public async Task BorrowedFactory_RemainsCallerOwned()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Data().GetProperty("submit_response")))); using var factory = new KlingTestHttpFactory(handler);
        var loki = Build(factory); await loki.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken); await loki.DisposeAsync(); Assert.False(factory.Disposed); Assert.Equal(1, factory.ClientsDisposed);
    }
}
