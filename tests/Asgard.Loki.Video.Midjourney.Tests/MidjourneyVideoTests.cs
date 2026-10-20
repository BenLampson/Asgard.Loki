using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.Midjourney.Tests.MidjourneyFixture;

namespace Asgard.Loki.Video.Midjourney.Tests;

/// <summary>独立原生请求、父任务、返回码和所有视频输出载体的公开 SDK 验证。</summary>
public sealed class MidjourneyVideoTests
{
    /// <summary>生成与延长的全部字段按原样发送，匿名/密钥身份明确，重建后查询原目标和任务。</summary>
    [Theory] [InlineData(0, false)] [InlineData(0, true)] [InlineData(1, false)] [InlineData(1, true)] [InlineData(2, false)] [InlineData(2, true)]
    public async Task PublicSdk_PreservesFullNativeWireAndTask(int index, bool anonymous)
    {
        var wire = Case(index).GetProperty("wire_request"); var calls = 0; using var handler = new StubHttpHandler(async (message, ct) =>
        {
            Assert.Null(message.Headers.Authorization); Assert.Equal("application/json", Assert.Single(message.Headers.Accept).MediaType);
            if (anonymous) Assert.False(message.Headers.Contains("mj-api-secret")); else Assert.Equal("fixture-mj-secret", Assert.Single(message.Headers.GetValues("mj-api-secret")));
            if (++calls == 1)
            {
                Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://mj.invalid/mj/submit/video", message.RequestUri!.AbsoluteUri);
                using var document = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(ct)); Assert.True(JsonElement.DeepEquals(wire, document.RootElement)); Assert.False(document.RootElement.TryGetProperty("model", out _));
                if (index == 2) { Assert.Equal(0, document.RootElement.GetProperty("index").GetInt32()); Assert.Equal("parent-9007199254740993", document.RootElement.GetProperty("taskId").GetString()); }
                return Json(Ack());
            }
            Assert.Equal(HttpMethod.Get, message.Method); Assert.Null(message.Content); Assert.Equal("https://mj.invalid/mj/task/mj-video-9007199254740993/fetch", message.RequestUri!.AbsoluteUri); return Json(Query(5));
        });
        var options = Options(wire.GetProperty("videoType").GetString()!, secret: anonymous ? null : "fixture-mj-secret"); var factory = new StubHttpClientFactory(handler);
        await using var original = Build(factory, options); var submitted = await original.Video.SubmitAsync(Request(index), TestContext.Current.CancellationToken); Assert.Equal(VideoTaskState.Submitted, submitted.Snapshot.State); Assert.True(JsonElement.DeepEquals(Ack(), submitted.Snapshot.Data));
        await using var restored = Build(factory, options); var current = await restored.Video.GetAsync(submitted.Reference, TestContext.Current.CancellationToken); Assert.Equal(submitted.Reference, current.Reference);
        Assert.Equal(VideoTaskState.Succeeded, current.Snapshot.State); Assert.True(JsonElement.DeepEquals(Query(5), current.Snapshot.Data)); Assert.Equal(2, current.Snapshot.Data.GetProperty("videoUrls").GetArrayLength());
        Assert.Equal("https://output.example/thumb-b.webp", current.Snapshot.Data.GetProperty("videoUrls")[1].GetProperty("thumbnail").GetString()); Assert.Equal(2, handler.Calls);
        Assert.Equal(Query(5).GetRawText(), Encoding.UTF8.GetString(current.Snapshot.HttpResponse!.Body.Span));
    }
    /// <summary>原生提交码不重写，既有任务可能仍在执行或已经完成，排队不等于成功。</summary>
    [Theory] [InlineData(0, VideoTaskState.Submitted)] [InlineData(1, VideoTaskState.Running)] [InlineData(2, VideoTaskState.Succeeded)]
    [InlineData(3, VideoTaskState.Queued)] [InlineData(4, VideoTaskState.Succeeded)]
    public async Task Acknowledgement_PreservesCodeAndImmediateStatus(int index, VideoTaskState expected)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Ack(index)))); var result = await new MidjourneyVideoAdapter(new StubHttpClientFactory(handler), Options()).SubmitAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State); Assert.True(JsonElement.DeepEquals(Ack(index), result.Data)); Assert.Equal(Ack(index).GetProperty("code").GetInt32(), result.Data.GetProperty("code").GetInt32()); Assert.Equal(expected == VideoTaskState.Succeeded, result.IsTerminal); Assert.Equal(1, handler.Calls);
    }
    /// <summary>已知原生状态和取消明确映射，弹窗或未来状态即使进度 100% 也不完成。</summary>
    [Theory] [InlineData(0, VideoTaskState.Queued)] [InlineData(1, VideoTaskState.Submitted)] [InlineData(2, VideoTaskState.Running)] [InlineData(3, VideoTaskState.Failed)]
    [InlineData(4, VideoTaskState.Cancelled)] [InlineData(5, VideoTaskState.Succeeded)] [InlineData(6, VideoTaskState.Unknown)] [InlineData(7, VideoTaskState.Unknown)]
    public async Task Query_RespectsIndependentNativeLifecycle(int index, VideoTaskState expected)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(index)))); var result = await new MidjourneyVideoAdapter(new StubHttpClientFactory(handler), Options()).GetAsync("mj-video-9007199254740993", TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State); Assert.Equal(Query(index).GetProperty("status").GetString(), result.NativeStatus); Assert.True(JsonElement.DeepEquals(Query(index), result.Data)); Assert.Equal(expected is VideoTaskState.Succeeded or VideoTaskState.Failed or VideoTaskState.Cancelled, result.IsTerminal);
    }
    /// <summary>完整保留单视频、列表和明确 VIDEO 任务的旧 imageUrl 载体，不生成额外链接。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Success_CarriersStayOriginal(int index)
    {
        var body = Data().GetProperty("success_variants")[index].GetProperty("body"); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body)));
        var result = await new MidjourneyVideoAdapter(new StubHttpClientFactory(handler), Options()).GetAsync("mj-video-9007199254740993", TestContext.Current.CancellationToken); Assert.Equal(VideoTaskState.Succeeded, result.State); Assert.True(JsonElement.DeepEquals(body, result.Data)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>调用者给出完整 HTTP/HTTPS 根和速度前缀，不根据提示词替换地址或处理不透明 ID。</summary>
    [Theory] [InlineData("https://mj.invalid:8443/prefix/mj-fast/mj/")] [InlineData("http://mj.invalid:8080/mj/")]
    public async Task ExplicitRootAndOpaqueId_ArePreserved(string root)
    {
        const string id = "opaque/a?b#c %素材"; var data = JsonNode.Parse(Query(2).GetRawText())!.AsObject(); data["id"] = id;
        using var handler = new StubHttpHandler((message, _) =>
        { Assert.Equal(root + "task/" + Uri.EscapeDataString(id) + "/fetch", message.RequestUri!.AbsoluteUri); Assert.Equal("sk-fictional-secret", Assert.Single(message.Headers.GetValues("mj-api-secret"))); return Task.FromResult(Json(data.ToJsonString())); });
        var result = await new MidjourneyVideoAdapter(new StubHttpClientFactory(handler), Options(root: root, secret: "sk-fictional-secret")).GetAsync(id, TestContext.Current.CancellationToken); Assert.Equal(id, result.TaskId);
    }
    /// <summary>实际视频类型、根、认证身份和策略漂移均在原任务查询 IO 前拒绝。</summary>
    [Theory] [InlineData("root")] [InlineData("type")] [InlineData("secret")] [InlineData("anonymous")]
    [InlineData("client")] [InlineData("request-limit")] [InlineData("response-limit")]
    public async Task TargetDrift_FailsBeforeQueryIo(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Ack()))); var factory = new StubHttpClientFactory(handler); await using var original = Build(factory); var task = await original.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        var options = kind switch
        {
            "root" => Options(root: "https://another.invalid/mj/"), "type" => Options(videoType: "vid_1.1_i2v_720"), "secret" => Options(secret: "another-secret"), "anonymous" => Options(secret: null),
            "client" => Options(client: "another-client"), "request-limit" => Options(requestLimit: 1024), _ => Options(responseLimit: 1024)
        };
        await using var changed = Build(factory, options); await Assert.ThrowsAsync<InvalidOperationException>(() => changed.Video.GetAsync(task.Reference, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生拒绝码即使有任务 ID 也不当作接受，HTTP/API 原始错误只调用一次。</summary>
    [Theory] [InlineData(0, 200, false)] [InlineData(1, 200, false)] [InlineData(2, 200, true)]
    [InlineData(0, 401, false)] [InlineData(1, 429, false)] [InlineData(2, 503, true)]
    public async Task RawFailure_IsNeverNormalizedRetriedOrCompleted(int index, int status, bool query)
    {
        var body = Data().GetProperty("api_errors")[index]; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body, (HttpStatusCode)status))); var adapter = new MidjourneyVideoAdapter(new StubHttpClientFactory(handler), Options());
        var error = await Assert.ThrowsAsync<VideoUpstreamException>(() => query ? adapter.GetAsync("task", TestContext.Current.CancellationToken) : adapter.SubmitAsync(Request(), TestContext.Current.CancellationToken)); Assert.Equal(status, (int)error.StatusCode); Assert.Equal(body.GetRawText(), Encoding.UTF8.GetString(error.ResponseBody.Span)); Assert.Equal(1, handler.Calls);
    }
}
