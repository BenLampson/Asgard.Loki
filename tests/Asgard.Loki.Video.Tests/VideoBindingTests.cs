using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Xunit;

namespace Asgard.Loki.Video.Tests;

/// <summary>通过公共入口验证独立 Video 路由、原生任务身份和完整目标绑定。</summary>
public sealed class VideoBindingTests
{
    /// <summary>首次任务可独立调度，既有任务不参与新的目标选择。</summary>
    [Fact]
    public async Task Query_UsesOriginalTaskTargetAfterOtherSubmissions()
    {
        var a = new StubVideoAdapter("a"); var b = new StubVideoAdapter("b"); await using var client = Build(a, b);
        var first = await client.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        var second = await client.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        await client.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal("a", first.Reference.TargetId); Assert.Equal("b", second.Reference.TargetId);
        var queried = await client.Video.GetAsync(first.Reference, TestContext.Current.CancellationToken);
        Assert.Equal(first.Reference, queried.Reference); Assert.Equal([first.Reference.TaskId], a.Queries); Assert.Empty(b.Queries);
        Assert.Equal(2, a.Requests.Count); Assert.Single(b.Requests);
    }
    /// <summary>应用可无凭据持久化引用；重建客户端后即使目标顺序改变仍查询同一上游。</summary>
    [Fact]
    public async Task PersistedReference_RestoresCompleteTargetWithoutSecretsOrPolling()
    {
        var a = new StubVideoAdapter("a"); var b = new StubVideoAdapter("b"); VideoTaskReference reference;
        await using (var first = Build(a, b))
        {
            reference = (await first.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken)).Reference;
            Assert.Empty(a.Queries); Assert.Empty(b.Queries);
        }
        Assert.Equal(0, a.Disposals); Assert.Equal(0, b.Disposals);
        var json = JsonSerializer.Serialize(reference); Assert.DoesNotContain("test-credential", json); Assert.DoesNotContain("server.invalid", json);
        var restored = JsonSerializer.Deserialize<VideoTaskReference>(json)!;
        await using var second = Build(b, a); await second.Video.GetAsync(restored, TestContext.Current.CancellationToken);
        Assert.Single(a.Queries); Assert.Empty(b.Queries);
    }
    /// <summary>服务器、实际模型、凭据或适配实现改变均使已持久化任务引用失效。</summary>
    [Theory] [InlineData("server")] [InlineData("model")] [InlineData("credential")] [InlineData("implementation")]
    public async Task ChangedCompleteTarget_RejectsQueryBeforeIo(string changed)
    {
        var adapter = new StubVideoAdapter(); VideoTaskReference reference;
        await using (var first = Build(adapter)) reference = (await first.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken)).Reference;
        switch (changed)
        {
            case "server": adapter.Server = "https://other.invalid/"; break;
            case "model": adapter.Model = "other-model"; break;
            case "credential": adapter.Credential = "other-credential"; break;
            case "implementation": adapter.Implementation = "version-two"; break;
        }
        await using var second = Build(adapter);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.Video.GetAsync(reference, TestContext.Current.CancellationToken)); Assert.Empty(adapter.Queries);
    }
    /// <summary>注册后漂移和查询返回错误任务都不能混入原结果。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ChangedLiveIdentityOrWrongTask_IsRejected(bool wrongTask)
    {
        var adapter = new StubVideoAdapter(); await using var client = Build(adapter);
        var task = await client.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        if (wrongTask)
        {
            adapter.ReturnedId = "foreign-task";
            await Assert.ThrowsAsync<InvalidDataException>(() => client.Video.GetAsync(task.Reference, TestContext.Current.CancellationToken));
        }
        else
        {
            adapter.Server = "https://other.invalid/";
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.Video.GetAsync(task.Reference, TestContext.Current.CancellationToken)); Assert.Empty(adapter.Queries);
        }
    }
    /// <summary>原任务目标被删除时不改选备用目标；原生提交错误也不重试。</summary>
    [Fact]
    public async Task RemovedOrFailedTarget_DoesNotFailOver()
    {
        var a = new StubVideoAdapter("a"); var b = new StubVideoAdapter("b"); VideoTaskReference reference;
        await using (var first = Build(a)) reference = (await first.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken)).Reference;
        await using var next = Build(b);
        await Assert.ThrowsAsync<InvalidOperationException>(() => next.Video.GetAsync(reference, TestContext.Current.CancellationToken)); Assert.Empty(b.Queries);
        a.BeforeSubmit = _ => throw new IOException("native failure"); await using var failed = Build(a, b);
        await Assert.ThrowsAsync<IOException>(() => failed.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken)); Assert.Empty(b.Requests);
    }
    /// <summary>不能用另一个公共别名误接同一目标下的任务。</summary>
    [Fact]
    public async Task ChangedPublicRoute_DoesNotRebindTask()
    {
        var adapter = new StubVideoAdapter(); await using var first = Build(adapter);
        var reference = (await first.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken)).Reference;
        await using var next = new LokiClientBuilder().AddVideo(new() { Routes = [new() { Model = "other", Targets = [adapter] }] }).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => next.Video.GetAsync(reference with { Model = "other" }, TestContext.Current.CancellationToken)); Assert.Empty(adapter.Queries);
    }
    /// <summary>先复制调用者原生字段后再等待，未知值、素材引用和大整数不被重写。</summary>
    [Fact]
    public async Task Submit_SnapshotsBeforeFirstAwait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new StubVideoAdapter { BeforeSubmit = async token => { entered.SetResult(); await release.Task.WaitAsync(token); } };
        await using var client = Build(adapter); var request = Request(); request.Parameters["future"] = JsonNode.Parse("{\"n\":9007199254740993,\"disabled\":false,\"optional\":null}");
        var expected = request.Parameters.ToJsonString(); var pending = client.Video.SubmitAsync(request, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken); request.Parameters.Clear(); release.SetResult(); var result = await pending;
        Assert.Equal(expected, Assert.Single(adapter.Requests).Parameters.ToJsonString()); Assert.Equal(VideoTaskState.Submitted, result.Snapshot.State); Assert.False(result.Snapshot.IsTerminal);
    }
    /// <summary>无效参数和重复目标在调用前拒绝，不借用其他路由。</summary>
    [Fact]
    public async Task InvalidConfigurationOrReservedModel_FailsBeforeIo()
    {
        var adapter = new StubVideoAdapter(); Assert.Throws<ArgumentException>(() => Build(adapter, adapter));
        await using var client = Build(adapter); var request = Request(); request.Parameters["model"] = "override";
        await Assert.ThrowsAsync<ArgumentException>(() => client.Video.SubmitAsync(request, TestContext.Current.CancellationToken)); Assert.Empty(adapter.Requests);
    }
    /// <summary>只使用真实公共模块机制，所有上游行为均由内存适配器记录。</summary>
    private static ILokiClient Build(params IVideoTaskAdapter[] targets)
        => new LokiClientBuilder().AddVideo(new() { Routes = [new() { Model = "public", Targets = targets }] }).Build();
    /// <summary>原生字段不引入统一提示词或素材协调层。</summary>
    private static VideoTaskRequest Request() => new() { Model = "public", Parameters = new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "hello" }) } };
}
