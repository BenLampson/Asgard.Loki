using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.MiniMax.Tests.HailuoFixture;

namespace Asgard.Loki.Video.MiniMax.Tests;

/// <summary>验证独立 v1/v2 原生请求、结果、任务关联和完整目标持久化。</summary>
public sealed class MiniMaxVideoTests
{
    /// <summary>完整原生文本、首尾帧和多模态输入保真，查询使用所选版本的原生路径和结构。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task SubmitAndQuery_PreserveFullNativeWire(int index)
    {
        var fixture = Data().GetProperty("submissions")[index]; var wire = fixture.GetProperty("wire_request");
        var version = Enum.Parse<MiniMaxVideoApiVersion>(fixture.GetProperty("version").GetString()!); var taskId = version == MiniMaxVideoApiVersion.V1 ? "hailuo-v1-01" : "hailuo-v2-01"; var calls = 0;
        using var handler = new StubHttpHandler(async (request, ct) =>
        {
            Assert.Equal("Bearer fixture-hailuo-key", request.Headers.Authorization!.ToString());
            if (++calls == 1)
            {
                Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("https://hailuo.invalid/" + (version == MiniMaxVideoApiVersion.V1 ? "v1" : "v2") + "/video_generation", request.RequestUri!.AbsoluteUri);
                Assert.Equal("application/json", request.Content!.Headers.ContentType!.ToString()); using var document = JsonDocument.Parse(await request.Content.ReadAsByteArrayAsync(ct));
                Assert.True(JsonElement.DeepEquals(wire, document.RootElement)); return Json(fixture.GetProperty("response"));
            }
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Content);
            Assert.Equal(version == MiniMaxVideoApiVersion.V1 ? "https://hailuo.invalid/v1/query/video_generation?task_id=" + taskId : "https://hailuo.invalid/v2/query/video_generation/" + taskId, request.RequestUri!.AbsoluteUri);
            return Json(Query(version, 5));
        });
        var factory = new StubHttpClientFactory(handler); var options = Options(version, wire.GetProperty("model").GetString()!); await using var loki = Build(factory, options);
        var task = await loki.Video.SubmitAsync(Request(index), TestContext.Current.CancellationToken); Assert.Equal(VideoTaskState.Submitted, task.Snapshot.State); Assert.False(task.Snapshot.IsTerminal); Assert.Null(task.Snapshot.NativeStatus);
        Assert.True(JsonElement.DeepEquals(fixture.GetProperty("response"), task.Snapshot.Data)); var persisted = JsonSerializer.Deserialize<VideoTaskReference>(JsonSerializer.Serialize(task.Reference))!;
        await using var rebuilt = Build(factory, options); var current = await rebuilt.Video.GetAsync(persisted, TestContext.Current.CancellationToken);
        Assert.Equal(VideoTaskState.Succeeded, current.Snapshot.State); Assert.True(JsonElement.DeepEquals(Query(version, 5), current.Snapshot.Data)); Assert.Equal(2, handler.Calls);
    }
    /// <summary>v1 状态保持大小写和原文，未来状态不猜测为失败或成功。</summary>
    [Theory] [InlineData(0, VideoTaskState.Running)] [InlineData(1, VideoTaskState.Queued)] [InlineData(2, VideoTaskState.Running)]
    [InlineData(3, VideoTaskState.Failed)] [InlineData(4, VideoTaskState.Unknown)] [InlineData(5, VideoTaskState.Succeeded)]
    public Task V1Lifecycle_PreservesFullSnapshot(int index, VideoTaskState expected) => CheckState(MiniMaxVideoApiVersion.V1, index, expected);
    /// <summary>v2 原生 task、错误、用量和未来状态不折叠到 v1 结果结构。</summary>
    [Theory] [InlineData(0, VideoTaskState.Queued)] [InlineData(1, VideoTaskState.Running)] [InlineData(2, VideoTaskState.Failed)]
    [InlineData(3, VideoTaskState.Cancelled)] [InlineData(4, VideoTaskState.Unknown)] [InlineData(5, VideoTaskState.Succeeded)]
    public Task V2Lifecycle_PreservesFullSnapshot(int index, VideoTaskState expected) => CheckState(MiniMaxVideoApiVersion.V2, index, expected);
    /// <summary>对照完整固定原生结果，终态不能来自 HTTP 成功或未知值。</summary>
    private static async Task CheckState(MiniMaxVideoApiVersion version, int index, VideoTaskState expected)
    {
        var native = Query(version, index); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(native))); var adapter = new MiniMaxVideoAdapter(new StubHttpClientFactory(handler), Options(version));
        var result = await adapter.GetAsync(version == MiniMaxVideoApiVersion.V1 ? "hailuo-v1-01" : "hailuo-v2-01", TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State); Assert.Equal(expected is VideoTaskState.Succeeded or VideoTaskState.Failed or VideoTaskState.Cancelled, result.IsTerminal); Assert.True(JsonElement.DeepEquals(native, result.Data));
        Assert.Equal((version == MiniMaxVideoApiVersion.V1 ? native : native.GetProperty("task")).GetProperty("status").GetString(), result.NativeStatus); Assert.Equal(1, handler.Calls);
    }
    /// <summary>显式版本固定接口，即使模型名称像另一版本也不进行猜测或切换。</summary>
    [Theory] [InlineData(MiniMaxVideoApiVersion.V1, "MiniMax-H3")] [InlineData(MiniMaxVideoApiVersion.V2, "MiniMax-Hailuo-2.3")]
    public async Task Version_IsExplicitAndTaskIdIsEscaped(MiniMaxVideoApiVersion version, string model)
    {
        const string id = "opaque/a?b#c %素材"; var native = JsonNode.Parse(Query(version, 0).GetRawText())!;
        if (version == MiniMaxVideoApiVersion.V1) native["task_id"] = id; else native["task"]!["id"] = id;
        using var handler = new StubHttpHandler((request, _) =>
        {
            var prefix = "https://hailuo.invalid:8443/prefix/"; var escaped = Uri.EscapeDataString(id);
            Assert.Equal(prefix + (version == MiniMaxVideoApiVersion.V1 ? "v1/query/video_generation?task_id=" : "v2/query/video_generation/") + escaped, request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json(native.ToJsonString()));
        });
        var adapter = new MiniMaxVideoAdapter(new StubHttpClientFactory(handler), Options(version, model, root: "https://hailuo.invalid:8443/prefix/"));
        var result = await adapter.GetAsync(id, TestContext.Current.CancellationToken); Assert.Equal(id, result.TaskId); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生 API 错误与 HTTP 错误完整保留，不自动重试。</summary>
    [Theory] [InlineData(MiniMaxVideoApiVersion.V1, 200)] [InlineData(MiniMaxVideoApiVersion.V1, 429)]
    [InlineData(MiniMaxVideoApiVersion.V2, 200)] [InlineData(MiniMaxVideoApiVersion.V2, 429)] [InlineData(MiniMaxVideoApiVersion.V2, 503)]
    public async Task ApiErrors_PreserveOriginalBytes(MiniMaxVideoApiVersion version, int status)
    {
        var native = Data().GetProperty(version == MiniMaxVideoApiVersion.V1 ? "v1_api_error" : "v2_api_error"); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(native, (HttpStatusCode)status)));
        var adapter = new MiniMaxVideoAdapter(new StubHttpClientFactory(handler), Options(version));
        var error = await Assert.ThrowsAsync<VideoUpstreamException>(() => adapter.GetAsync("task", TestContext.Current.CancellationToken));
        Assert.Equal(status, (int)error.StatusCode); Assert.Equal(native.GetRawText(), Encoding.UTF8.GetString(error.ResponseBody.Span)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>持久化目标引用不允许迁移到其他主机、模型、凭据、版本或资源策略。</summary>
    [Theory] [InlineData("root")] [InlineData("model")] [InlineData("key")] [InlineData("version")] [InlineData("client")] [InlineData("request-limit")] [InlineData("response-limit")]
    public async Task TargetDrift_FailsBeforeQueryIo(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Data().GetProperty("submissions")[0].GetProperty("response")))); var factory = new StubHttpClientFactory(handler);
        await using var original = Build(factory); var created = await original.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        var options = change switch
        {
            "root" => Options(root: "https://other.invalid/"), "model" => Options(model: "other-model"), "key" => Options(key: "other-key"),
            "version" => Options(version: MiniMaxVideoApiVersion.V2), "client" => Options(client: "other-client"), "request-limit" => Options(requestLimit: 1000), _ => Options(responseLimit: 1000)
        };
        await using var rebuilt = Build(factory, options); await Assert.ThrowsAsync<InvalidOperationException>(() => rebuilt.Video.GetAsync(created.Reference, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>直接供应商调用也先完整快照，等待期间修改不能改变请求。</summary>
    [Theory] [InlineData(0)] [InlineData(3)]
    public async Task Submit_SnapshotsBeforeWait(int index)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = Data().GetProperty("submissions")[index]; var version = Enum.Parse<MiniMaxVideoApiVersion>(fixture.GetProperty("version").GetString()!);
        using var handler = new StubHttpHandler(async (request, ct) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(ct); using var document = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(ct)); Assert.True(JsonElement.DeepEquals(fixture.GetProperty("wire_request"), document.RootElement)); return Json(fixture.GetProperty("response"));
        });
        var adapter = new MiniMaxVideoAdapter(new StubHttpClientFactory(handler), Options(version, fixture.GetProperty("wire_request").GetProperty("model").GetString()!)); var request = Request(index);
        var pending = adapter.SubmitAsync(request, TestContext.Current.CancellationToken); await entered.Task.WaitAsync(TestContext.Current.CancellationToken); request.Parameters.Clear(); release.SetResult(); await pending;
    }
}
