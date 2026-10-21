using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Audio.Tests;

/// <summary>配置、JSON 与生命周期边界不借用供应商实现。</summary>
public sealed class AudioTaskValidationTests
{
    /// <summary>注册时验证所有路由与备用目标，不延迟到某次轮换才发现。</summary>
    [Theory]
    [InlineData("null-routes")] [InlineData("null-route")] [InlineData("empty-model")] [InlineData("null-targets")]
    [InlineData("empty-targets")] [InlineData("null-target")] [InlineData("duplicate-target")] [InlineData("duplicate-route")]
    [InlineData("empty-id")] [InlineData("empty-identity")]
    public void Registration_RejectsMalformedTaskConfiguration(string kind)
    {
        var adapter = new StubAudioTaskAdapter(); AudioTaskRoute route = new() { Model = "music", Targets = [adapter] };
        if (kind == "empty-id") adapter.Id = "";
        if (kind == "empty-identity") adapter.BindingIdentity = "";
        route = kind switch
        {
            "empty-model" => route with { Model = "" }, "null-targets" => route with { Targets = null! },
            "empty-targets" => route with { Targets = [] }, "null-target" => route with { Targets = [null!] },
            "duplicate-target" => route with { Targets = [adapter, new StubAudioTaskAdapter()] }, _ => route
        };
        var options = new AudioOptions { TaskRoutes = kind switch
        { "null-routes" => null!, "null-route" => [null!], "duplicate-route" => [route, route], _ => [route] } };
        Assert.ThrowsAny<ArgumentException>(() => new LokiClientBuilder().AddAudio(options));
    }

    /// <summary>外部列表清空不会改变已注册目标，每个客户端仍从首目标开始。</summary>
    [Fact]
    public async Task Registration_CopiesListsAndSeparatesClientSelection()
    {
        var first = new StubAudioTaskAdapter("first"); var second = new StubAudioTaskAdapter("second");
        var targets = new List<IAudioTaskAdapter> { first, second }; var routes = new List<AudioTaskRoute> { new() { Model = "music", Targets = targets } };
        var builder = new LokiClientBuilder().AddAudio(new() { TaskRoutes = routes }); targets.Clear(); routes.Clear();
        await using var one = builder.Build(); await using var two = builder.Build(); var token = TestContext.Current.CancellationToken;
        Assert.Equal("first", (await one.Audio.Tasks.SubmitAsync(AudioTaskClientTests.Request(), token)).Reference.TargetId);
        Assert.Equal("second", (await one.Audio.Tasks.SubmitAsync(AudioTaskClientTests.Request(), token)).Reference.TargetId);
        Assert.Equal("first", (await two.Audio.Tasks.SubmitAsync(AudioTaskClientTests.Request(), token)).Reference.TargetId);
    }

    /// <summary>未配置的任务入口不会借用已配置的语音或文件目标。</summary>
    [Fact]
    public async Task UnconfiguredTaskClient_FailsExplicitly()
    {
        await using var client = AudioFileFixture.Build(new()); Assert.Throws<InvalidOperationException>(() => client.Audio.Tasks);
    }

    /// <summary>无效公共参数、别名覆盖和非法 Unicode 均在调用适配器之前失败。</summary>
    [Theory]
    [InlineData("model")] [InlineData("operation")] [InlineData("parameters")] [InlineData("model-alias")]
    [InlineData("unicode-model")] [InlineData("unicode-operation")] [InlineData("unicode-field")] [InlineData("unicode-value")]
    public async Task Request_RejectsInvalidShapeBeforeDispatch(string kind)
    {
        var adapter = new StubAudioTaskAdapter(); await using var client = AudioTaskClientTests.Build(adapter);
        var request = AudioTaskClientTests.Request(); request = kind switch
        {
            "model" => request with { Model = "" }, "operation" => request with { Operation = "" },
            "parameters" => request with { Parameters = null! }, "unicode-model" => request with { Model = "\ud800" },
            "unicode-operation" => request with { Operation = "\ud800" }, _ => request
        };
        if (kind == "model-alias") request.Parameters["MoDeL"] = "override";
        if (kind == "unicode-field") request.Parameters["\ud800"] = "bad";
        if (kind == "unicode-value") request.Parameters["bad"] = new JsonArray { "\ud800" };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.Audio.Tasks.SubmitAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, adapter.Submits);
    }

    /// <summary>文档释放后仍保留原文、精确整数、空值，且数组集合独立复制。</summary>
    [Fact]
    public void Snapshots_CloneNativeDocumentAndBatchCollections()
    {
        const string raw = "{ \"unknown\":[null,false,0,9007199254740993],\"lyrics\":\"line1\\r\\nline2\" }";
        using var document = JsonDocument.Parse(raw);
        var snapshot = new AudioTaskSnapshot("id", "LYRICS", "SUCCESS", AudioTaskState.Succeeded, document.RootElement);
        var tasks = new List<AudioTaskSnapshot> { snapshot }; var batch = new AudioTaskBatchSnapshot(tasks, document.RootElement);
        tasks.Clear(); document.Dispose(); Assert.Single(batch.Tasks); Assert.Equal(raw, batch.Data.GetRawText()); Assert.Equal(raw, snapshot.Data.GetRawText());
    }

    /// <summary>只有明确终态完成；未知/已有局部音频仍不是成功。</summary>
    [Theory]
    [InlineData(AudioTaskState.Unknown, false)] [InlineData(AudioTaskState.Submitted, false)] [InlineData(AudioTaskState.Queued, false)]
    [InlineData(AudioTaskState.Running, false)] [InlineData(AudioTaskState.Succeeded, true)] [InlineData(AudioTaskState.Failed, true)]
    [InlineData(AudioTaskState.Cancelled, true)] [InlineData(AudioTaskState.Expired, true)]
    public void Lifecycle_OnlyExplicitStatesAreTerminal(AudioTaskState state, bool terminal)
        => Assert.Equal(terminal, StubAudioTaskAdapter.Window("id", "MUSIC", state).IsTerminal);

    /// <summary>无效状态、重复字段与非对象不能变成有效原生窗口。</summary>
    [Theory] [InlineData("array")] [InlineData("duplicate")] [InlineData("state")]
    public void Snapshot_RejectsMalformedNativeWindows(string kind)
    {
        using var document = JsonDocument.Parse(kind == "array" ? "[]" : kind == "duplicate" ? "{\"x\":1,\"x\":2}" : "{}");
        Assert.ThrowsAny<Exception>(() => new AudioTaskSnapshot("id", "MUSIC", null,
            kind == "state" ? (AudioTaskState)999 : AudioTaskState.Unknown, document.RootElement));
    }

    /// <summary>空批量响应只报告缺失，不捏造任何任务或终态。</summary>
    [Fact]
    public async Task EmptyNativeBatch_ReportsAllMissingReferences()
    {
        var adapter = new StubAudioTaskAdapter { Batch = _ => new([], StubAudioTaskAdapter.Json("{\"code\":\"success\",\"data\":[]}")) };
        await using var client = AudioTaskClientTests.Build(adapter); var token = TestContext.Current.CancellationToken;
        var reference = (await client.Audio.Tasks.SubmitAsync(AudioTaskClientTests.Request(), token)).Reference;
        var result = await client.Audio.Tasks.GetBatchAsync([reference], token); Assert.Empty(result.Tasks); Assert.Equal(new[] { reference }, result.MissingReferences);
    }

    /// <summary>独立结果不接受非对象或含重复字段的模糊信封。</summary>
    [Theory] [InlineData("[]")] [InlineData("{\"x\":1,\"x\":2}")]
    public async Task RawResult_RejectsAmbiguousEnvelope(string raw)
    {
        var adapter = new StubAudioTaskAdapter { Result = _ => StubAudioTaskAdapter.Json(raw) };
        await using var client = AudioTaskClientTests.Build(adapter); var token = TestContext.Current.CancellationToken;
        var reference = (await client.Audio.Tasks.SubmitAsync(AudioTaskClientTests.Request(), token)).Reference;
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Audio.Tasks.GetResultAsync(reference, token));
    }
}
