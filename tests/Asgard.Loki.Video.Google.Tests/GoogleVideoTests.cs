using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.Google.Tests.VeoFixture;

namespace Asgard.Loki.Video.Google.Tests;

/// <summary>验证 Gemini REST 原生字段、操作关联及完整目标持久化。</summary>
public sealed class GoogleVideoTests
{
    /// <summary>文本、首尾帧、参考图和视频延长正文完整发送，GET 不重写操作名称。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task SubmitAndQuery_PreserveFullNativeWire(int index)
    {
        using var handler = new StubHttpHandler(async (message, ct) =>
        {
            Assert.Null(message.Headers.Authorization); Assert.Equal("fixture-veo-key", message.Headers.GetValues("x-goog-api-key").Single());
            if (message.Method == HttpMethod.Post)
            {
                Assert.Equal("https://veo.invalid/v1beta/models/" + Model + ":predictLongRunning", message.RequestUri!.AbsoluteUri);
                using var document = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(ct));
                Assert.True(JsonElement.DeepEquals(Data().GetProperty("submissions")[index], document.RootElement));
                Assert.False(document.RootElement.TryGetProperty("model", out _)); Assert.Equal("application/json", message.Content.Headers.ContentType!.ToString()); return Json(Query(0));
            }
            Assert.Equal(HttpMethod.Get, message.Method); Assert.Equal("https://veo.invalid/v1beta/" + Operation, message.RequestUri!.AbsoluteUri); Assert.Null(message.Content); return Json(Query(4));
        });
        var factory = new StubHttpClientFactory(handler); await using var loki = Build(factory);
        var created = await loki.Video.SubmitAsync(Request(index), TestContext.Current.CancellationToken);
        Assert.Equal(Operation, created.Snapshot.TaskId); Assert.Equal(VideoTaskState.Running, created.Snapshot.State); Assert.Null(created.Snapshot.NativeStatus);
        var reference = JsonSerializer.Deserialize<VideoTaskReference>(JsonSerializer.Serialize(created.Reference))!;
        await using var rebuilt = Build(factory); var current = await rebuilt.Video.GetAsync(reference, TestContext.Current.CancellationToken);
        Assert.Equal(VideoTaskState.Succeeded, current.Snapshot.State); Assert.True(JsonElement.DeepEquals(Query(4), current.Snapshot.Data)); Assert.Equal(2, handler.Calls);
    }
    /// <summary>done 缺省或 false 不构成终态；过滤与原生错误不变为成功视频。</summary>
    [Theory] [InlineData(0, VideoTaskState.Running)] [InlineData(1, VideoTaskState.Running)] [InlineData(2, VideoTaskState.Failed)]
    [InlineData(3, VideoTaskState.Failed)] [InlineData(4, VideoTaskState.Succeeded)]
    public async Task NativeLifecycle_PreservesAllData(int index, VideoTaskState expected)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(index)))); var adapter = new GoogleVideoAdapter(new StubHttpClientFactory(handler), Options());
        var result = await adapter.GetAsync(Operation, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State); Assert.Equal(index >= 2, result.IsTerminal); Assert.True(JsonElement.DeepEquals(Query(index), result.Data));
        Assert.Equal(index == 0 ? null : index == 1 ? "false" : "true", result.NativeStatus); Assert.Equal(1, handler.Calls);
    }
    /// <summary>提交也可以立即完成，不能丢失原生视频列表或错误。</summary>
    [Theory] [InlineData(2)] [InlineData(4)]
    public async Task ImmediateCompletion_UsesSameNativeParser(int index)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(index)))); var adapter = new GoogleVideoAdapter(new StubHttpClientFactory(handler), Options());
        var result = await adapter.SubmitAsync(Request(), TestContext.Current.CancellationToken); Assert.True(result.IsTerminal); Assert.True(JsonElement.DeepEquals(Query(index), result.Data));
    }
    /// <summary>混合过滤与有效样本仍保留全部数据，不把过滤计数误当作整单失败。</summary>
    [Fact]
    public async Task PartialFilteredResult_DoesNotDiscardValidSamples()
    {
        var data = JsonNode.Parse(Query(4).GetRawText())!; data["response"]!["generateVideoResponse"]!["raiMediaFilteredCount"] = 1;
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(data.ToJsonString()))); var adapter = new GoogleVideoAdapter(new StubHttpClientFactory(handler), Options());
        var result = await adapter.GetAsync(Operation, TestContext.Current.CancellationToken); Assert.Equal(VideoTaskState.Succeeded, result.State);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(data), result.Data));
    }
    /// <summary>HTTP/API 失败保留原文和状态，只发送一次，不切换目标。</summary>
    [Theory] [InlineData(200)] [InlineData(429)] [InlineData(503)]
    public async Task ApiFailure_IsLosslessAndNeverRetried(int status)
    {
        var body = Data().GetProperty("api_error"); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body, (HttpStatusCode)status)));
        var adapter = new GoogleVideoAdapter(new StubHttpClientFactory(handler), Options());
        var error = await Assert.ThrowsAsync<VideoUpstreamException>(() => adapter.GetAsync(Operation, TestContext.Current.CancellationToken));
        Assert.Equal(status, (int)error.StatusCode); Assert.Equal(body.GetRawText(), Encoding.UTF8.GetString(error.ResponseBody.Span)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>持久化操作不能迁移至另一服务器、模型、凭据、客户端或资源限制。</summary>
    [Theory] [InlineData("root")] [InlineData("model")] [InlineData("key")] [InlineData("client")] [InlineData("json-limit")]
    [InlineData("content-limit")] [InlineData("redirects")]
    public async Task TargetDrift_FailsBeforeQueryIo(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(0)))); var factory = new StubHttpClientFactory(handler);
        await using var original = Build(factory); var created = await original.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        var changed = change switch
        {
            "root" => Options(root: "https://other.invalid/v1beta/"), "model" => Options(model: "other-model"), "key" => Options(key: "other-key"),
            "client" => Options(client: "other-client"), "json-limit" => Options(responseLimit: 1000), "content-limit" => Options(contentLimit: 10), _ => Options(redirects: 1)
        };
        await using var rebuilt = Build(factory, changed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rebuilt.Video.GetAsync(created.Reference, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>快照在网络等待前完成，调用者修改不改变已选定的完整请求。</summary>
    [Fact]
    public async Task Submit_SnapshotsBeforeWait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHttpHandler(async (message, ct) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(ct); using var document = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(ct));
            Assert.True(JsonElement.DeepEquals(Data().GetProperty("submissions")[0], document.RootElement)); return Json(Query(0));
        });
        var adapter = new GoogleVideoAdapter(new StubHttpClientFactory(handler), Options()); var request = Request();
        var pending = adapter.SubmitAsync(request, TestContext.Current.CancellationToken); await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        request.Parameters.Clear(); release.SetResult(); await pending;
    }
}
