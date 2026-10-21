using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.GoogleVertex.Tests.VertexVideoFixture;

namespace Asgard.Loki.Video.GoogleVertex.Tests;

/// <summary>验证 Vertex 原生提交和 POST 查询，不把其他协议字段带入资源调用。</summary>
public sealed class VertexVideoTests
{
    /// <summary>完整请求与结果按原生 JSON 发送，凭据同主体轮换后仍复用持久化任务。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task SubmitAndFetch_PreserveNativeProtocolAndTokenRotation(int index)
    {
        var calls = 0; var clock = new VertexVideoClock(); using var source = new VertexVideoTokenSource(clock);
        using var handler = new StubHttpHandler(async (message, ct) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("Bearer fixture-token-" + ++calls, message.Headers.Authorization!.ToString()); Assert.False(message.Headers.Contains("x-goog-api-key"));
            Assert.Equal("quota-fixture", message.Headers.GetValues("x-goog-user-project").Single()); Assert.Equal("application/json", message.Content!.Headers.ContentType!.ToString());
            using var document = JsonDocument.Parse(await message.Content.ReadAsByteArrayAsync(ct));
            if (calls == 1)
            {
                Assert.Equal("https://vertex.invalid/v1/" + Resource + ":predictLongRunning", message.RequestUri!.AbsoluteUri);
                Assert.True(JsonElement.DeepEquals(Data().GetProperty("submissions")[index], document.RootElement)); return Json(Query(0));
            }
            Assert.Equal("https://vertex.invalid/v1/" + Resource + ":fetchPredictOperation", message.RequestUri!.AbsoluteUri);
            Assert.True(JsonElement.DeepEquals(Data().GetProperty("query_request"), document.RootElement)); return Json(Query(4));
        });
        var factory = new StubHttpClientFactory(handler); var options = Options(quota: "quota-fixture");
        await using var loki = Build(factory, source, clock, options); var created = await loki.Video.SubmitAsync(Request(index), TestContext.Current.CancellationToken);
        Assert.Equal(Operation, created.Snapshot.TaskId); Assert.Equal(VideoTaskState.Running, created.Snapshot.State); Assert.Null(created.Snapshot.NativeStatus);
        var reference = JsonSerializer.Deserialize<VideoTaskReference>(JsonSerializer.Serialize(created.Reference))!;
        await using var rebuilt = Build(factory, source, clock, options); var current = await rebuilt.Video.GetAsync(reference, TestContext.Current.CancellationToken);
        Assert.Equal(VideoTaskState.Succeeded, current.Snapshot.State); Assert.True(JsonElement.DeepEquals(Query(4), current.Snapshot.Data)); Assert.Equal(2, handler.Calls); Assert.Equal(2, source.Calls); Assert.False(source.Disposed);
    }
    /// <summary>done 的缺省语义、原生错误、过滤、GCS 输出和内联字节均保持完整。</summary>
    [Theory] [InlineData(0, VideoTaskState.Running)] [InlineData(1, VideoTaskState.Running)] [InlineData(2, VideoTaskState.Failed)]
    [InlineData(3, VideoTaskState.Failed)] [InlineData(4, VideoTaskState.Succeeded)] [InlineData(5, VideoTaskState.Succeeded)]
    public async Task NativeOperation_PreservesFullResult(int index, VideoTaskState expected)
    {
        var clock = new VertexVideoClock(); using var source = new VertexVideoTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(index))));
        var adapter = new GoogleVertexVideoAdapter(new StubHttpClientFactory(handler), source, Options(), clock); var result = await adapter.GetAsync(Operation, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State); Assert.True(JsonElement.DeepEquals(Query(index), result.Data)); Assert.Equal(index >= 2, result.IsTerminal);
        Assert.Equal(index == 0 ? null : index == 1 ? "false" : "true", result.NativeStatus); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生部分过滤保留全部有效输出，不丢弃多视频或过滤计数。</summary>
    [Fact]
    public async Task PartialFiltering_DoesNotDiscardOutputs()
    {
        var data = JsonNode.Parse(Query(4).GetRawText())!; data["response"]!["raiMediaFilteredCount"] = 1;
        var clock = new VertexVideoClock(); using var source = new VertexVideoTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(data.ToJsonString())));
        var result = await new GoogleVertexVideoAdapter(new StubHttpClientFactory(handler), source, Options(), clock).GetAsync(Operation, TestContext.Current.CancellationToken);
        Assert.Equal(VideoTaskState.Succeeded, result.State); Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(data), result.Data));
    }
    /// <summary>立即完成的提交遵守相同结果和终态语义。</summary>
    [Theory] [InlineData(2)] [InlineData(4)] [InlineData(5)]
    public async Task ImmediateCompletion_PreservesResult(int index)
    {
        var clock = new VertexVideoClock(); using var source = new VertexVideoTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(index))));
        var result = await new GoogleVertexVideoAdapter(new StubHttpClientFactory(handler), source, Options(), clock).SubmitAsync(Request(), TestContext.Current.CancellationToken);
        Assert.True(result.IsTerminal); Assert.True(JsonElement.DeepEquals(Query(index), result.Data));
    }
    /// <summary>HTTP/API 错误完整保留，来源和模型请求都不会自动重试。</summary>
    [Theory] [InlineData(200)] [InlineData(403)] [InlineData(429)] [InlineData(503)]
    public async Task NativeFailure_PreservesOriginalBytesWithoutRetry(int status)
    {
        var clock = new VertexVideoClock(); using var source = new VertexVideoTokenSource(clock); var data = Data().GetProperty("api_error");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(data, (HttpStatusCode)status))); var adapter = new GoogleVertexVideoAdapter(new StubHttpClientFactory(handler), source, Options(), clock);
        var error = await Assert.ThrowsAsync<VideoUpstreamException>(() => adapter.GetAsync(Operation, TestContext.Current.CancellationToken));
        Assert.Equal(status, (int)error.StatusCode); Assert.Equal(data.GetRawText(), Encoding.UTF8.GetString(error.ResponseBody.Span)); Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls);
    }
    /// <summary>重建后不能借同名任务换项目、区域、模型、主体、认证来源或资源策略。</summary>
    [Theory] [InlineData("project")] [InlineData("location")] [InlineData("model")] [InlineData("root")] [InlineData("principal")]
    [InlineData("source")] [InlineData("quota")] [InlineData("client")] [InlineData("request-limit")] [InlineData("response-limit")]
    public async Task TargetDrift_FailsBeforeTokenOrHttp(string change)
    {
        var clock = new VertexVideoClock(); using var source = new VertexVideoTokenSource(clock); using var changedSource = new VertexVideoTokenSource(clock);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(0)))); var factory = new StubHttpClientFactory(handler);
        await using var original = Build(factory, source, clock); var task = await original.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        var changed = change switch
        {
            "project" => Options(resource: Resource.Replace("fixture-project", "other-project")), "location" => Options(resource: Resource.Replace("us-central1", "europe-west2")),
            "model" => Options(resource: Resource.Replace("veo-3.1-generate-001", "other-model")), "root" => Options(root: "https://other.invalid/v1/"),
            "principal" => Options(principal: "other-principal"), "quota" => Options(quota: "other-quota"), "client" => Options(client: "other-client"),
            "request-limit" => Options(requestLimit: 1000), "response-limit" => Options(responseLimit: 1000), _ => Options()
        };
        if (change == "source") changedSource.Version = "other-source";
        await using var rebuilt = Build(factory, changedSource, clock, changed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rebuilt.Video.GetAsync(task.Reference, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls); Assert.Equal(0, changedSource.Calls);
    }
}
