using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Xunit;

namespace Asgard.Loki.Audio.Tests;

/// <summary>任务上下文、完整目标与原生字段的独立音频契约。</summary>
public sealed class AudioTaskClientTests
{
    internal static AudioTaskRequest Request(string operation = "MUSIC") => new()
    { Model = "music", Operation = operation, Parameters = JsonNode.Parse("{\"prompt\":\"原词\\r\\n第二行\",\"unknown\":[null,false,0,9007199254740993]}")!.AsObject() };
    internal static ILokiClient Build(params IAudioTaskAdapter[] targets) => new LokiClientBuilder().AddAudio(new()
    { TaskRoutes = [new() { Model = "music", Targets = targets }] }).Build();

    /// <summary>不配置语音也能独立使用音乐，快照不依赖调用者后续修改。</summary>
    [Fact]
    public async Task Submit_SnapshotsNativeParametersAndBorrowsAdapter()
    {
        var adapter = new StubAudioTaskAdapter(); var request = Request(); var original = request.Parameters.ToJsonString();
        adapter.OnCall = () => request.Parameters.Clear();
        var client = Build(adapter); var task = await client.Audio.Tasks.SubmitAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(original, adapter.LastRequest!.Parameters.ToJsonString()); Assert.NotSame(request.Parameters, adapter.LastRequest.Parameters);
        Assert.Equal("MUSIC", task.Reference.Operation); Assert.Equal("music", task.Reference.Model); Assert.Equal("native", task.Reference.TargetId);
        Assert.DoesNotContain("credential", task.Reference.Fingerprint); Assert.Equal(64, task.Reference.Fingerprint.Length);
        Assert.Equal("9007199254740993", task.Snapshot.Data.GetProperty("unknown")[3].GetRawText());
        Assert.Throws<InvalidOperationException>(() => client.Audio.Speech); Assert.Throws<InvalidOperationException>(() => client.Audio.Transcriptions);
        Assert.Throws<InvalidOperationException>(() => client.Audio.Translations);
        await client.DisposeAsync(); Assert.False(adapter.Disposed);
    }

    /// <summary>新提交轮换目标，查询始终恢复任务绑定的目标。</summary>
    [Fact]
    public async Task Query_UsesOriginalTargetAndCanRestoreEquivalentConfiguration()
    {
        var first = new StubAudioTaskAdapter("one"); var second = new StubAudioTaskAdapter("two");
        await using var client = Build(first, second); var token = TestContext.Current.CancellationToken;
        var one = await client.Audio.Tasks.SubmitAsync(Request(), token); var two = await client.Audio.Tasks.SubmitAsync(Request("LYRICS"), token);
        Assert.Equal("one", one.Reference.TargetId); Assert.Equal("two", two.Reference.TargetId);
        var query = await client.Audio.Tasks.GetAsync(one.Reference, token); Assert.Equal(one.Reference, query.Reference);
        Assert.Equal(1, first.Gets); Assert.Equal(0, second.Gets);
        var restored = new StubAudioTaskAdapter("one"); await using var next = Build(restored);
        await next.Audio.Tasks.GetAsync(one.Reference, token); Assert.Equal(1, restored.Gets);
        Assert.Equal("one", (await next.Audio.Tasks.SubmitAsync(Request(), token)).Reference.TargetId);
    }

    /// <summary>操作、凭据/模型/服务器摘要或目标篡改均在调用前失败。</summary>
    [Theory] [InlineData("operation")] [InlineData("fingerprint")] [InlineData("target")] [InlineData("route")] [InlineData("identity")]
    public async Task ChangedBinding_RejectsBeforeQuery(string change)
    {
        var adapter = new StubAudioTaskAdapter(); await using var client = Build(adapter);
        var reference = (await client.Audio.Tasks.SubmitAsync(Request(), TestContext.Current.CancellationToken)).Reference;
        reference = change switch
        {
            "operation" => reference with { Operation = "LYRICS" }, "fingerprint" => reference with { Fingerprint = "other" },
            "target" => reference with { TargetId = "other" }, "route" => reference with { Model = "other" }, _ => reference
        };
        if (change == "identity") adapter.BindingIdentity += "-changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Audio.Tasks.GetAsync(reference, TestContext.Current.CancellationToken));
        Assert.Equal(0, adapter.Gets);
    }

    /// <summary>调用后的配置漂移也不能发布为原任务。</summary>
    [Theory] [InlineData("submit")] [InlineData("get")] [InlineData("batch")] [InlineData("result")]
    public async Task IdentityDriftDuringCall_RejectsResult(string operation)
    {
        var adapter = new StubAudioTaskAdapter(); await using var client = Build(adapter); var token = TestContext.Current.CancellationToken;
        var reference = (await client.Audio.Tasks.SubmitAsync(Request(), token)).Reference;
        adapter.OnCall = () => adapter.BindingIdentity += "changed";
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (operation == "submit") await client.Audio.Tasks.SubmitAsync(Request(), token);
            else if (operation == "get") await client.Audio.Tasks.GetAsync(reference, token);
            else if (operation == "batch") await client.Audio.Tasks.GetBatchAsync([reference], token);
            else await client.Audio.Tasks.GetResultAsync(reference, token);
        });
    }

    /// <summary>任务 ID 和操作分别验证，不能把音乐与歌词的成功结果混用。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Query_MismatchedAssociationFails(bool wrongId)
    {
        var adapter = new StubAudioTaskAdapter { Get = query => StubAudioTaskAdapter.Window(wrongId ? "other" : query.TaskId, wrongId ? query.Operation : "LYRICS") };
        await using var client = Build(adapter); var reference = (await client.Audio.Tasks.SubmitAsync(Request(), TestContext.Current.CancellationToken)).Reference;
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Audio.Tasks.GetAsync(reference, TestContext.Current.CancellationToken));
    }

    /// <summary>提交关联操作错误不会创建错误引用，也不会切换其他目标。</summary>
    [Fact]
    public async Task Submit_WrongOperationAndFailureNeverFailOver()
    {
        var adapter = new StubAudioTaskAdapter { Submit = _ => Task.FromResult(StubAudioTaskAdapter.Window("id", "LYRICS")) }; var spare = new StubAudioTaskAdapter("spare");
        await using var client = Build(adapter, spare);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Audio.Tasks.SubmitAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(1, adapter.Submits); Assert.Equal(0, spare.Submits);
        adapter.Submit = _ => throw new IOException("fixture failure"); await using var fresh = Build(adapter, spare);
        await Assert.ThrowsAsync<IOException>(() => fresh.Audio.Tasks.SubmitAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(2, adapter.Submits); Assert.Equal(0, spare.Submits);
    }

    /// <summary>批量支持同目标的不同原生操作，关联按请求顺序，原始 JSON 顺序不变。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Batch_OrdersOnlyKnownResultsAndReportsMissing(bool partial)
    {
        var adapter = new StubAudioTaskAdapter(); await using var client = Build(adapter); var token = TestContext.Current.CancellationToken;
        var music = (await client.Audio.Tasks.SubmitAsync(Request(), token)).Reference; var lyrics = (await client.Audio.Tasks.SubmitAsync(Request("LYRICS"), token)).Reference;
        if (partial) adapter.Batch = queries => new([StubAudioTaskAdapter.Window(queries[1].TaskId, queries[1].Operation)], StubAudioTaskAdapter.Json("{ \"data\":[\"second\"],\"future\":false }"));
        var result = await client.Audio.Tasks.GetBatchAsync([music, lyrics], token);
        Assert.Equal(partial ? [lyrics] : new[] { music, lyrics }, result.Tasks.Select(task => task.Reference));
        Assert.Equal(partial ? new[] { music } : [], result.MissingReferences);
        Assert.Equal(new[] { new AudioTaskQuery(music.TaskId, "MUSIC"), new AudioTaskQuery(lyrics.TaskId, "LYRICS") }, adapter.LastQueries);
        Assert.Equal(1, adapter.Batches); Assert.Equal(0, adapter.Gets);
        Assert.Equal(partial ? "{ \"data\":[\"second\"],\"future\":false }" : "{\"native\":[2,1]}", result.Data.GetRawText());
    }

    /// <summary>批量不接受其他任务、重复结果或错误操作；缺失是明确结果而非错误终态。</summary>
    [Theory] [InlineData("extra")] [InlineData("duplicate")] [InlineData("operation")]
    public async Task Batch_RejectsInvalidAssociations(string kind)
    {
        var adapter = new StubAudioTaskAdapter(); await using var client = Build(adapter); var token = TestContext.Current.CancellationToken;
        var reference = (await client.Audio.Tasks.SubmitAsync(Request(), token)).Reference;
        adapter.Batch = _ => new(kind switch
        {
            "extra" => [StubAudioTaskAdapter.Window("other", "MUSIC")],
            "duplicate" => [StubAudioTaskAdapter.Window(reference.TaskId, "MUSIC"), StubAudioTaskAdapter.Window(reference.TaskId, "MUSIC")],
            _ => [StubAudioTaskAdapter.Window(reference.TaskId, "LYRICS")]
        }, StubAudioTaskAdapter.Json("{}"));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Audio.Tasks.GetBatchAsync([reference], token));
    }

    /// <summary>跨目标、重复输入和缺少可选原生接口均不发请求，不模拟批量或状态。</summary>
    [Fact]
    public async Task Batch_RejectsMixedTargetsDuplicateInputsAndMissingNativeSupport()
    {
        var one = new StubAudioTaskAdapter("one"); var two = new StubAudioTaskAdapter("two"); var token = TestContext.Current.CancellationToken;
        await using var client = Build(one, two); var first = (await client.Audio.Tasks.SubmitAsync(Request(), token)).Reference;
        var second = (await client.Audio.Tasks.SubmitAsync(Request(), token)).Reference;
        await Assert.ThrowsAsync<ArgumentException>(() => client.Audio.Tasks.GetBatchAsync([first, second], token));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Audio.Tasks.GetBatchAsync([first, first], token));
        Assert.Equal(0, one.Batches + two.Batches);
        var basic = new BasicAudioTaskAdapter(); await using var other = Build(basic); var reference = (await other.Audio.Tasks.SubmitAsync(Request(), token)).Reference;
        await Assert.ThrowsAsync<NotSupportedException>(() => other.Audio.Tasks.GetBatchAsync([reference], token));
        await Assert.ThrowsAsync<NotSupportedException>(() => other.Audio.Tasks.GetResultAsync(reference, token)); Assert.Equal(0, basic.Gets);
    }

    /// <summary>独立结果只保留完整原生 JSON；音频 URL 不用来推断终态。</summary>
    [Fact]
    public async Task RawResult_RetainsOriginalEnvelopeAndReference()
    {
        var adapter = new StubAudioTaskAdapter(); await using var client = Build(adapter); var token = TestContext.Current.CancellationToken;
        var reference = (await client.Audio.Tasks.SubmitAsync(Request(), token)).Reference;
        var result = await client.Audio.Tasks.GetResultAsync(reference, token); Assert.Equal(reference, result.Reference);
        Assert.Equal("https://fixture.invalid/a?sig=A%2fB", result.Data.GetProperty("data")[0].GetProperty("audio_url").GetString());
        Assert.False(result.Data.TryGetProperty("status", out _)); Assert.Equal(1, adapter.Results); Assert.Equal(0, adapter.Gets);
    }

    /// <summary>取消在调用前和调用后均有效，不能先返回成功。</summary>
    [Theory] [InlineData("submit")] [InlineData("get")] [InlineData("batch")] [InlineData("result")]
    public async Task CancellationBeforeOrDuringCall_DoesNotPublish(string operation)
    {
        var adapter = new StubAudioTaskAdapter(); await using var client = Build(adapter);
        var reference = (await client.Audio.Tasks.SubmitAsync(Request(), TestContext.Current.CancellationToken)).Reference;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        async Task Call(CancellationToken token)
        {
            if (operation == "submit") await client.Audio.Tasks.SubmitAsync(Request(), token);
            else if (operation == "get") await client.Audio.Tasks.GetAsync(reference, token);
            else if (operation == "batch") await client.Audio.Tasks.GetBatchAsync([reference], token);
            else await client.Audio.Tasks.GetResultAsync(reference, token);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(cancelled.Token)); Assert.Equal(1, adapter.Submits + adapter.Gets + adapter.Batches + adapter.Results);
        using var during = new CancellationTokenSource(); adapter.OnCall = during.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(during.Token));
        Assert.Equal(2, adapter.Submits + adapter.Gets + adapter.Batches + adapter.Results);
    }
}
