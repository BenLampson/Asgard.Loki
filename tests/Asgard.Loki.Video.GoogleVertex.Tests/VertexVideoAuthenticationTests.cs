using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.GoogleVertex.Tests.VertexVideoFixture;

namespace Asgard.Loki.Video.GoogleVertex.Tests;

/// <summary>稳定主体、认证源身份、令牌时效与借用资源的确定性测试。</summary>
public sealed class VertexVideoAuthenticationTests
{
    /// <summary>不同主体、过期、来源配置漂移及认证失败都不能发送模型请求。</summary>
    [Theory] [InlineData("principal")] [InlineData("expired")] [InlineData("source")] [InlineData("fault")] [InlineData("null")]
    public async Task InvalidAccess_FailsWithoutHttp(string kind)
    {
        var clock = new VertexVideoClock(); using var source = new VertexVideoTokenSource(clock);
        source.Resolve = (principal, _) =>
        {
            if (kind == "fault") throw new IOException("fixture auth failure");
            if (kind == "source") source.Version = "changed-source";
            return ValueTask.FromResult(kind == "null" ? null! : new GoogleVertexVideoAccessToken(kind == "principal" ? "other" : principal, "fictional-token", kind == "expired" ? clock.Now : clock.Now.AddHours(1)));
        };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new VertexVideoHttpFactory(handler);
        var adapter = new GoogleVertexVideoAdapter(factory, source, Options(), clock);
        if (kind == "fault") await Assert.ThrowsAsync<IOException>(() => adapter.GetAsync(Operation, TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.GetAsync(Operation, TestContext.Current.CancellationToken));
        Assert.Equal(1, source.Calls); Assert.Equal(0, handler.Calls); Assert.Equal(0, factory.ClientsCreated); Assert.False(source.Disposed);
    }
    /// <summary>来源配置在取令牌前已改变时，不取令牌也不创建客户端。</summary>
    [Fact]
    public async Task ChangedSourceIdentity_FailsBeforeAuthentication()
    {
        var clock = new VertexVideoClock(); using var source = new VertexVideoTokenSource(clock); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        var adapter = new GoogleVertexVideoAdapter(new StubHttpClientFactory(handler), source, Options(), clock); source.Version = "changed-source";
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.GetAsync(Operation, TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Equal(0, handler.Calls);
    }
    /// <summary>HTTP 工厂创建期间令牌过期，发送前第二次时效校验必须阻止请求。</summary>
    [Fact]
    public async Task TokenExpiresBeforeSend_DoesNotSendOrRefresh()
    {
        var clock = new VertexVideoClock(); using var source = new VertexVideoTokenSource(clock)
        { Resolve = (principal, _) => ValueTask.FromResult(new GoogleVertexVideoAccessToken(principal, "fictional-token", clock.Now.AddSeconds(1))) };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        using var factory = new VertexVideoHttpFactory(handler) { OnCreate = _ => clock.Now = clock.Now.AddSeconds(2) };
        var adapter = new GoogleVertexVideoAdapter(factory, source, Options(), clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.GetAsync(Operation, TestContext.Current.CancellationToken));
        Assert.Equal(1, source.Calls); Assert.Equal(1, factory.ClientsDisposed); Assert.Equal(0, handler.Calls);
    }
    /// <summary>默认头不能引入未绑定的 API Key 或原生请求项目。</summary>
    [Theory] [InlineData("x-goog-api-key")] [InlineData("api-key")] [InlineData("x-goog-user-project")]
    public async Task UnboundDefaultIdentity_IsRejected(string header)
    {
        var clock = new VertexVideoClock(); using var source = new VertexVideoTokenSource(clock); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        using var factory = new VertexVideoHttpFactory(handler) { OnCreate = client => client.DefaultRequestHeaders.Add(header, "unbound-fixture") };
        var adapter = new GoogleVertexVideoAdapter(factory, source, Options(), clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.GetAsync(Operation, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls); Assert.Equal(1, source.Calls);
    }
    /// <summary>不从资源项目推断 quota project，显式配置时也不继承默认不同值。</summary>
    [Theory] [InlineData(null)] [InlineData("chosen-quota")]
    public async Task QuotaProject_IsExplicit(string? quota)
    {
        var clock = new VertexVideoClock(); using var source = new VertexVideoTokenSource(clock);
        using var handler = new StubHttpHandler((request, _) =>
        {
            if (quota is null) Assert.False(request.Headers.Contains("x-goog-user-project"));
            else Assert.Equal(quota, request.Headers.GetValues("x-goog-user-project").Single());
            return Task.FromResult(Json(Query(0)));
        });
        using var factory = new VertexVideoHttpFactory(handler) { OnCreate = client => { if (quota is not null) client.DefaultRequestHeaders.Add("x-goog-user-project", "unbound-default"); } };
        await new GoogleVertexVideoAdapter(factory, source, Options(quota: quota), clock).GetAsync(Operation, TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>请求在认证源等待前复制；认证源忽略取消也不能导致后续 HTTP 发送。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AuthenticationWait_SnapshotsAndHonorsCancellation(bool cancel)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new VertexVideoClock(); using var source = new VertexVideoTokenSource(clock);
        source.Resolve = async (principal, _) => { entered.SetResult(); await release.Task; return new(principal, "fictional-token", clock.Now.AddHours(1)); };
        using var cancellation = new CancellationTokenSource(); using var handler = new StubHttpHandler(async (request, ct) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(ct)); Assert.True(JsonElement.DeepEquals(Data().GetProperty("submissions")[0], document.RootElement)); return Json(Query(0));
        });
        var adapter = new GoogleVertexVideoAdapter(new StubHttpClientFactory(handler), source, Options(), clock); var request = Request(); var pending = adapter.SubmitAsync(request, cancellation.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken); request.Parameters.Clear(); if (cancel) cancellation.Cancel(); release.SetResult();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); else await pending; Assert.Equal(cancel ? 0 : 1, handler.Calls);
    }
    /// <summary>默认 JSON、显示、绑定和持久化引用均不包含短期令牌。</summary>
    [Fact]
    public async Task Secrets_StayOutOfPublicMetadata()
    {
        var clock = new VertexVideoClock(); var token = new GoogleVertexVideoAccessToken("fixture-principal", "sensitive-fixture-token", clock.Now.AddHours(1));
        using var source = new VertexVideoTokenSource(clock) { Resolve = (_, _) => ValueTask.FromResult(token) }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(0))));
        var factory = new StubHttpClientFactory(handler); var adapter = new GoogleVertexVideoAdapter(factory, source, Options(), clock);
        await using var loki = Build(factory, source, clock); var result = await loki.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(token.AccessToken, JsonSerializer.Serialize(token)); Assert.DoesNotContain(token.AccessToken, token.ToString());
        Assert.DoesNotContain(token.AccessToken, adapter.BindingIdentity); Assert.DoesNotContain(token.AccessToken, JsonSerializer.Serialize(result.Reference));
        Assert.Throws<ArgumentException>(() => new GoogleVertexVideoAccessToken("fixture-principal", "sensitive\r\ntoken", clock.Now.AddHours(1)));
    }
    /// <summary>完整结果在最后释放、来源漂移及最终取消完成之前不得交付。</summary>
    [Theory] [InlineData("fault")] [InlineData("cancel")] [InlineData("source")]
    public async Task FinalDisposalAndSourceCheck_PreventInvalidDelivery(string kind)
    {
        var clock = new VertexVideoClock(); using var source = new VertexVideoTokenSource(clock); using var cancel = new CancellationTokenSource();
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(4))));
        using var factory = new VertexVideoHttpFactory(handler) { OnDispose = () => { if (kind == "fault") throw new IOException("fixture dispose"); if (kind == "cancel") cancel.Cancel(); else source.Version = "changed-source"; } };
        var adapter = new GoogleVertexVideoAdapter(factory, source, Options(), clock);
        if (kind == "fault") await Assert.ThrowsAsync<IOException>(() => adapter.GetAsync(Operation, cancel.Token));
        else if (kind == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetAsync(Operation, cancel.Token));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.GetAsync(Operation, cancel.Token));
        Assert.Equal(1, handler.Calls); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>SDK 模块和调用不拥有工厂或令牌源；新建客户端在结果返回前已释放。</summary>
    [Fact]
    public async Task BorrowedResources_RemainCallerOwned()
    {
        var clock = new VertexVideoClock(); using var source = new VertexVideoTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(0)))); using var factory = new VertexVideoHttpFactory(handler);
        var loki = Build(factory, source, clock); await loki.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken); await loki.DisposeAsync();
        Assert.False(source.Disposed); Assert.False(factory.Disposed); Assert.Equal(1, factory.ClientsDisposed);
    }
}
