using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.Ali.Tests.WanFixture;

namespace Asgard.Loki.Video.Ali.Tests;

/// <summary>验证原生万相视频完整请求、结果、任务关联与不可变完整目标。</summary>
public sealed class AliVideoTests
{
    /// <summary>逐一核对文本、首尾帧、语音驱动输入和显式服务；查询只执行一次且不下载媒体。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task NativeSubmitAndQuery_PreserveEntireWire(int index)
    {
        var fixture = Data().GetProperty("submissions")[index]; var expected = fixture.GetProperty("wire_request");
        var operation = Enum.Parse<AliVideoOperation>(fixture.GetProperty("operation").GetString()!); var calls = 0;
        using var handler = new StubHttpHandler(async (message, ct) =>
        {
            Assert.Equal("Bearer fixture-wan-key", message.Headers.Authorization!.ToString());
            if (++calls == 1)
            {
                var service = operation == AliVideoOperation.VideoGeneration ? "video-generation" : "image2video";
                Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://wan.invalid/api/v1/services/aigc/" + service + "/video-synthesis", message.RequestUri!.AbsoluteUri);
                Assert.Equal("enable", message.Headers.GetValues("X-DashScope-Async").Single()); Assert.Equal("application/json", message.Content!.Headers.ContentType!.ToString());
                using var document = JsonDocument.Parse(await message.Content.ReadAsByteArrayAsync(ct)); Assert.True(JsonElement.DeepEquals(expected, document.RootElement));
                Assert.Equal(9007199254740993L, document.RootElement.GetProperty("parameters").GetProperty("Future").GetProperty("large").GetInt64());
                return Json(fixture.GetProperty("response"));
            }
            Assert.Equal(HttpMethod.Get, message.Method); Assert.Equal("https://wan.invalid/api/v1/tasks/wan-fixture-01", message.RequestUri!.AbsoluteUri);
            Assert.False(message.Headers.Contains("X-DashScope-Async")); Assert.Null(message.Content); return Json(Query(6));
        });
        var options = Options(model: expected.GetProperty("model").GetString()!, operation: operation); var factory = new StubHttpClientFactory(handler);
        await using var loki = Build(factory, options); var task = await loki.Video.SubmitAsync(Request(index), TestContext.Current.CancellationToken);
        Assert.Equal(VideoTaskState.Queued, task.Snapshot.State); Assert.False(task.Snapshot.IsTerminal);
        var persisted = JsonSerializer.Deserialize<VideoTaskReference>(JsonSerializer.Serialize(task.Reference))!;
        await using var rebuilt = Build(factory, options); var current = await rebuilt.Video.GetAsync(persisted, TestContext.Current.CancellationToken);
        Assert.Equal(VideoTaskState.Succeeded, current.Snapshot.State); Assert.True(current.Snapshot.IsTerminal); Assert.True(JsonElement.DeepEquals(Query(6), current.Snapshot.Data)); Assert.Equal(2, handler.Calls);
    }
    /// <summary>原生失败与未来状态不会被 HTTP 200 抹去，UNKNOWN 不被猜测为已过期或已失败。</summary>
    [Theory] [InlineData(0, VideoTaskState.Queued)] [InlineData(1, VideoTaskState.Running)] [InlineData(2, VideoTaskState.Failed)]
    [InlineData(3, VideoTaskState.Cancelled)] [InlineData(4, VideoTaskState.Unknown)] [InlineData(5, VideoTaskState.Unknown)] [InlineData(6, VideoTaskState.Succeeded)]
    public async Task NativeStatusAndFullData_RemainIntact(int index, VideoTaskState state)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(index)))); var adapter = new AliVideoAdapter(new StubHttpClientFactory(handler), Options());
        var result = await adapter.GetAsync("wan-fixture-01", TestContext.Current.CancellationToken);
        Assert.Equal(state, result.State); Assert.Equal(Query(index).GetProperty("output").GetProperty("task_status").GetString(), result.NativeStatus);
        Assert.True(JsonElement.DeepEquals(Query(index), result.Data)); Assert.Equal(state is VideoTaskState.Succeeded or VideoTaskState.Failed or VideoTaskState.Cancelled, result.IsTerminal);
    }
    /// <summary>HTTP 和原生 API 错误保留原文，不自动重试或更换上游。</summary>
    [Theory] [InlineData(200)] [InlineData(429)]
    public async Task ApiErrors_PreserveOriginalResponse(int status)
    {
        var data = Data().GetProperty("api_error"); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(data, (HttpStatusCode)status)));
        var adapter = new AliVideoAdapter(new StubHttpClientFactory(handler), Options());
        var error = await Assert.ThrowsAsync<VideoUpstreamException>(() => adapter.SubmitAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(status, (int)error.StatusCode); Assert.Equal(data.GetRawText(), System.Text.Encoding.UTF8.GetString(error.ResponseBody.Span)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>持久化引用不能在重建后移动到不同服务器、模型、凭据、原生服务或资源策略。</summary>
    [Theory] [InlineData("root")] [InlineData("model")] [InlineData("key")] [InlineData("operation")] [InlineData("policy")]
    public async Task TargetDrift_FailsBeforeQueryIo(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Ack()))); var factory = new StubHttpClientFactory(handler);
        await using var original = Build(factory); var task = await original.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        var options = change switch
        {
            "root" => Options(root: "https://other.invalid/api/v1/"), "model" => Options(model: "other"), "key" => Options(key: "other-fixture-key"),
            "operation" => Options(operation: AliVideoOperation.ImageToVideo), _ => Options(responseLimit: 1000)
        };
        await using var changed = Build(factory, options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => changed.Video.GetAsync(task.Reference, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>服务由选项明确选择，实际模型名称与任务标识不改变路径规则。</summary>
    [Fact]
    public async Task ExplicitServiceAndEscapedTaskId_DoNotUseHeuristics()
    {
        const string id = "opaque/a?b#c %素材"; var calls = 0;
        using var handler = new StubHttpHandler((message, _) =>
        {
            if (++calls == 1) Assert.Equal("/prefix/api/v1/services/aigc/image2video/video-synthesis", message.RequestUri!.AbsolutePath);
            else Assert.Equal("https://wan.invalid:8443/prefix/api/v1/tasks/" + Uri.EscapeDataString(id), message.RequestUri!.AbsoluteUri);
            var data = JsonNode.Parse(Ack().GetRawText())!; data["output"]!["task_id"] = id; return Task.FromResult(Json(data.ToJsonString()));
        });
        var adapter = new AliVideoAdapter(new StubHttpClientFactory(handler), Options(model: "no-model-name-routing", operation: AliVideoOperation.ImageToVideo, root: "https://wan.invalid:8443/prefix/api/v1/"));
        var created = await adapter.SubmitAsync(Request(), TestContext.Current.CancellationToken); await adapter.GetAsync(created.TaskId, TestContext.Current.CancellationToken); Assert.Equal(2, handler.Calls);
    }
    /// <summary>原生 results 地址位置保持原状，不搬运到另一字段或下载结果。</summary>
    [Fact]
    public async Task NativeNestedResultUrl_IsPreserved()
    {
        var data = JsonNode.Parse(Query(6).GetRawText())!; var output = data["output"]!.AsObject(); var url = output["video_url"]!.DeepClone(); output.Remove("video_url"); output["results"] = new JsonObject { ["video_url"] = url, ["Future"] = false };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(data.ToJsonString()))); var adapter = new AliVideoAdapter(new StubHttpClientFactory(handler), Options());
        var result = await adapter.GetAsync("wan-fixture-01", TestContext.Current.CancellationToken); Assert.Equal(VideoTaskState.Succeeded, result.State);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(data), result.Data)); Assert.Equal(1, handler.Calls);
    }
}
