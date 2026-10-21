using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.Kling.Tests.KlingFixture;

namespace Asgard.Loki.Video.Kling.Tests;

/// <summary>独立原生文本/图片请求、状态、输出和完整目标持久化验证。</summary>
public sealed class KlingVideoTests
{
    /// <summary>两种显式操作和认证都完整保留原生正文，只写 model_name，不写旧 model 或默认设置。</summary>
    [Theory] [InlineData(0, KlingVideoAuthentication.ApiKey)] [InlineData(1, KlingVideoAuthentication.ApiKey)]
    [InlineData(0, KlingVideoAuthentication.AccessKeyJwt)] [InlineData(1, KlingVideoAuthentication.AccessKeyJwt)]
    public async Task SubmitAndQuery_PreserveNativeWire(int index, KlingVideoAuthentication authentication)
    {
        var fixture = Data().GetProperty("submissions")[index]; var operation = Enum.Parse<KlingVideoOperation>(fixture.GetProperty("operation").GetString()!);
        var path = index == 0 ? "text2video" : "image2video"; var calls = 0;
        using var handler = new StubHttpHandler(async (message, ct) =>
        {
            var token = authentication == KlingVideoAuthentication.ApiKey ? "fixture-api-key" : Data().GetProperty("jwt_vector").GetProperty("token").GetString();
            Assert.Equal("Bearer " + token, message.Headers.Authorization!.ToString());
            if (++calls == 1)
            {
                Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://kling.invalid/v1/videos/" + path, message.RequestUri!.AbsoluteUri);
                using var document = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(ct)); Assert.True(JsonElement.DeepEquals(fixture.GetProperty("wire_request"), document.RootElement));
                Assert.False(document.RootElement.TryGetProperty("model", out _)); Assert.Equal("application/json", message.Content.Headers.ContentType!.ToString()); return Json(Data().GetProperty("submit_response"));
            }
            Assert.Equal(HttpMethod.Get, message.Method); Assert.Equal("https://kling.invalid/v1/videos/" + path + "/kling-fixture-01", message.RequestUri!.AbsoluteUri); Assert.Null(message.Content); return Json(Query(4));
        });
        var options = Options(operation, authentication); var factory = new StubHttpClientFactory(handler); await using var original = Build(factory, options);
        var task = await original.Video.SubmitAsync(Request(index), TestContext.Current.CancellationToken); Assert.Equal(VideoTaskState.Submitted, task.Snapshot.State); Assert.Equal("submitted", task.Snapshot.NativeStatus);
        Assert.True(JsonElement.DeepEquals(Data().GetProperty("submit_response"), task.Snapshot.Data)); var persisted = JsonSerializer.Deserialize<VideoTaskReference>(JsonSerializer.Serialize(task.Reference))!;
        await using var rebuilt = Build(factory, options); var current = await rebuilt.Video.GetAsync(persisted, TestContext.Current.CancellationToken);
        Assert.Equal(VideoTaskState.Succeeded, current.Snapshot.State); Assert.True(JsonElement.DeepEquals(Query(4), current.Snapshot.Data));
        Assert.Equal("3.50", current.Snapshot.Data.GetProperty("data").GetProperty("final_unit_deduction").GetString()); Assert.Equal(2, current.Snapshot.Data.GetProperty("data").GetProperty("task_result").GetProperty("videos").GetArrayLength()); Assert.Equal(2, handler.Calls);
    }
    /// <summary>原生失败/未来状态和每个视频的 ID、地址、时长都不被 HTTP 200 或统一投影抹去。</summary>
    [Theory] [InlineData(0, VideoTaskState.Submitted)] [InlineData(1, VideoTaskState.Running)] [InlineData(2, VideoTaskState.Failed)]
    [InlineData(3, VideoTaskState.Unknown)] [InlineData(4, VideoTaskState.Succeeded)]
    public async Task NativeLifecycle_PreservesFullSnapshot(int index, VideoTaskState expected)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(index)))); var adapter = new KlingVideoAdapter(new StubHttpClientFactory(handler), Options(), new KlingTestClock());
        var actual = await adapter.GetAsync("kling-fixture-01", TestContext.Current.CancellationToken); Assert.Equal(expected, actual.State); Assert.True(JsonElement.DeepEquals(Query(index), actual.Data));
        Assert.Equal(expected is VideoTaskState.Succeeded or VideoTaskState.Failed, actual.IsTerminal); Assert.Equal(Query(index).GetProperty("data").GetProperty("task_status").GetString(), actual.NativeStatus); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生提交也可以带立即失败或完成窗口，不能只固定返回排队状态。</summary>
    [Theory] [InlineData(2)] [InlineData(4)]
    public async Task NativeImmediateStatus_IsRetained(int index)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(index)))); var adapter = new KlingVideoAdapter(new StubHttpClientFactory(handler), Options(), new KlingTestClock());
        var actual = await adapter.SubmitAsync(Request(), TestContext.Current.CancellationToken); Assert.True(actual.IsTerminal); Assert.True(JsonElement.DeepEquals(Query(index), actual.Data));
    }
    /// <summary>没有原生状态的提交确认表示接受，不补造任务状态。</summary>
    [Fact]
    public async Task BareAcknowledgement_DoesNotInventNativeStatus()
    {
        var body = JsonNode.Parse(Data().GetProperty("submit_response").GetRawText())!; body["data"]!.AsObject().Remove("task_status");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body.ToJsonString()))); var adapter = new KlingVideoAdapter(new StubHttpClientFactory(handler), Options(), new KlingTestClock());
        var actual = await adapter.SubmitAsync(Request(), TestContext.Current.CancellationToken); Assert.Equal(VideoTaskState.Submitted, actual.State); Assert.Null(actual.NativeStatus); Assert.False(actual.IsTerminal);
    }
    /// <summary>HTTP/原生 API 错误保持原始字节，不重试、换目标或伪造任务终态。</summary>
    [Theory] [InlineData(200)] [InlineData(401)] [InlineData(429)] [InlineData(503)]
    public async Task ApiFailure_IsLosslessAndNeverRetried(int status)
    {
        var body = Data().GetProperty("api_error"); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body, (HttpStatusCode)status))); var adapter = new KlingVideoAdapter(new StubHttpClientFactory(handler), Options(), new KlingTestClock());
        var error = await Assert.ThrowsAsync<VideoUpstreamException>(() => adapter.GetAsync("task", TestContext.Current.CancellationToken));
        Assert.Equal(status, (int)error.StatusCode); Assert.Equal(body.GetRawText(), Encoding.UTF8.GetString(error.ResponseBody.Span)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>图片参数不能切换固定文本服务，任务 ID 也只作为一个转义路径段传递。</summary>
    [Fact]
    public async Task ExplicitOperation_DoesNotUseImageHeuristics()
    {
        const string id = "opaque/a?b#c %素材"; var calls = 0;
        using var handler = new StubHttpHandler((message, _) =>
        {
            var body = JsonNode.Parse(Data().GetProperty("submit_response").GetRawText())!; body["data"]!["task_id"] = id;
            Assert.Equal("https://kling.invalid:8443/prefix/v1/videos/text2video" + (++calls == 1 ? "" : "/" + Uri.EscapeDataString(id)), message.RequestUri!.AbsoluteUri); return Task.FromResult(Json(body.ToJsonString()));
        });
        var adapter = new KlingVideoAdapter(new StubHttpClientFactory(handler), Options(root: "https://kling.invalid:8443/prefix/"), new KlingTestClock());
        var created = await adapter.SubmitAsync(Request(1), TestContext.Current.CancellationToken); await adapter.GetAsync(created.TaskId, TestContext.Current.CancellationToken); Assert.Equal(2, handler.Calls);
    }
    /// <summary>重建后的任务不能更换服务器、模型、服务、认证方式或凭据身份。</summary>
    [Theory] [InlineData("root")] [InlineData("model")] [InlineData("operation")] [InlineData("authentication")]
    [InlineData("access")] [InlineData("secret")] [InlineData("client")] [InlineData("request-limit")] [InlineData("response-limit")]
    public async Task TargetDrift_FailsBeforeQueryIo(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Data().GetProperty("submit_response")))); var factory = new StubHttpClientFactory(handler);
        await using var original = Build(factory); var task = await original.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        var changed = change switch
        {
            "root" => Options(root: "https://other.invalid/"), "model" => Options(model: "other-model"), "operation" => Options(operation: KlingVideoOperation.ImageToVideo),
            "authentication" => Options(authentication: KlingVideoAuthentication.ApiKey), "access" => Options(access: "other-access"), "secret" => Options(secret: "other-secret"),
            "client" => Options(client: "other-client"), "request-limit" => Options(requestLimit: 1000), _ => Options(responseLimit: 1000)
        };
        await using var rebuilt = Build(factory, changed); await Assert.ThrowsAsync<InvalidOperationException>(() => rebuilt.Video.GetAsync(task.Reference, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
}
