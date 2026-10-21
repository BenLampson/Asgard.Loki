using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.Volcengine.Tests.ArkFixture;

namespace Asgard.Loki.Video.Volcengine.Tests;

/// <summary>原生提交、查询、结果和持久化目标关联的离线公共 SDK 验证。</summary>
public sealed class ArkVideoTests
{
    /// <summary>完整最终请求保留各类素材、asset 引用、未知参数、零值和原生签名结果。</summary>
    [Fact]
    public async Task SubmitAndQuery_PreserveIndependentFullWireAndNativeResult()
    {
        var methods = new List<HttpMethod>(); var paths = new List<string>(); JsonObject? body = null;
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            methods.Add(message.Method); paths.Add(message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer fixture-key-only", message.Headers.Authorization!.ToString());
            if (message.Method == HttpMethod.Post)
            {
                body = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!.AsObject();
                return Json(Data().GetProperty("submitted"));
            }
            Assert.Null(message.Content); return Json(Query(6));
        });
        await using var client = Build(new StubHttpClientFactory(handler)); var request = Request(); var before = request.Parameters.ToJsonString();
        var submitted = await client.Video.SubmitAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(VideoTaskState.Submitted, submitted.Snapshot.State); Assert.False(submitted.Snapshot.IsTerminal); Assert.Equal(1, handler.Calls);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Data().GetProperty("wire_request").GetRawText()), body)); Assert.Equal(before, request.Parameters.ToJsonString());
        var reference = JsonSerializer.Deserialize<VideoTaskReference>(JsonSerializer.Serialize(submitted.Reference))!;
        var result = await client.Video.GetAsync(reference, TestContext.Current.CancellationToken);
        Assert.Equal(Query(6).GetRawText(), result.Snapshot.Data.GetRawText()); Assert.Equal(VideoTaskState.Succeeded, result.Snapshot.State); Assert.True(result.Snapshot.IsTerminal);
        Assert.Equal([HttpMethod.Post,HttpMethod.Get], methods);
        Assert.Equal(["https://ark.invalid/api/v3/contents/generations/tasks","https://ark.invalid/api/v3/contents/generations/tasks/task_ark_one"], paths);
        Assert.Equal(2, handler.Calls); // 返回的媒体 URL 不触发 SDK 额外下载。
    }
    /// <summary>HTTP 200 的原生失败或未知状态不会变成成功，也不启动隐式重试/轮询。</summary>
    [Theory]
    [InlineData(0,VideoTaskState.Queued,false)] [InlineData(1,VideoTaskState.Running,false)]
    [InlineData(2,VideoTaskState.Failed,true)] [InlineData(3,VideoTaskState.Expired,true)]
    [InlineData(4,VideoTaskState.Cancelled,true)] [InlineData(5,VideoTaskState.Unknown,false)]
    public async Task NativeStatusAndError_ArePreserved(int index, VideoTaskState state, bool terminal)
    {
        using var handler = new StubHttpHandler((_,_) => Task.FromResult(Json(Query(index))));
        var adapter = new VolcengineVideoAdapter(new StubHttpClientFactory(handler),Options());
        var result = await adapter.GetAsync("task_ark_one",TestContext.Current.CancellationToken);
        Assert.Equal(state,result.State); Assert.Equal(terminal,result.IsTerminal); Assert.Equal(Query(index).GetRawText(),result.Data.GetRawText()); Assert.Equal(1,handler.Calls);
    }
    /// <summary>任务 ID 作为一个路径段编码，包含版本/代理前缀的目标根不丢失。</summary>
    [Fact]
    public async Task Get_EscapesOnlyTaskSegmentAndKeepsConfiguredRoot()
    {
        const string id = "task/a?query=#你好"; var query = JsonNode.Parse(Query(0).GetRawText())!.AsObject(); query["id"] = id;
        using var handler = new StubHttpHandler((message,_) =>
        {
            Assert.Equal("https://ark.invalid:8443/proxy/api/v3/contents/generations/tasks/"+Uri.EscapeDataString(id),message.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json(query.ToJsonString()));
        });
        var adapter = new VolcengineVideoAdapter(new StubHttpClientFactory(handler),Options(root:"https://ark.invalid:8443/proxy/api/v3"));
        Assert.Equal(id,(await adapter.GetAsync(id,TestContext.Current.CancellationToken)).TaskId); Assert.Equal(1,handler.Calls);
    }
    /// <summary>HTTP/API 错误保留原始字节和状态，不把根级错误当生成任务失败或提交成功。</summary>
    [Theory] [InlineData(HttpStatusCode.Unauthorized)] [InlineData(HttpStatusCode.OK)]
    public async Task ApiErrors_KeepOriginalStatusAndBytesWithoutRetry(HttpStatusCode status)
    {
        var body = Data().GetProperty("http_error").GetRawText();
        using var handler = new StubHttpHandler((_,_) => Task.FromResult(Json(body,status)));
        var adapter = new VolcengineVideoAdapter(new StubHttpClientFactory(handler),Options());
        var error = await Assert.ThrowsAsync<VideoUpstreamException>(() => adapter.SubmitAsync(Request(),TestContext.Current.CancellationToken));
        Assert.Equal(status,error.StatusCode); Assert.Equal(System.Text.Encoding.UTF8.GetBytes(body),error.ResponseBody.ToArray()); Assert.Equal(1,handler.Calls);
        var changed = error.ResponseBody.ToArray(); changed[0] = 0; Assert.Equal((byte)'{',error.ResponseBody.Span[0]);
    }
    /// <summary>查询身份、状态和成功结果必须齐全，不能接受错误任务或空结果作为成功。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"id\":\"task_ark_one\"}")]
    [InlineData("{\"id\":\"task_ark_one\",\"status\":false}")]
    [InlineData("{\"id\":\"foreign\",\"status\":\"queued\"}")]
    [InlineData("{\"id\":\"task_ark_one\",\"status\":\"succeeded\",\"content\":{}}")]
    [InlineData("{\"id\":\"task_ark_one\",\"status\":\"succeeded\",\"content\":{\"video_url\":null}}")]
    public async Task InvalidNativeTask_FailsBeforePublishingSuccess(string body)
    {
        using var handler = new StubHttpHandler((_,_) => Task.FromResult(Json(body)));
        var adapter = new VolcengineVideoAdapter(new StubHttpClientFactory(handler),Options());
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetAsync("task_ark_one",TestContext.Current.CancellationToken)); Assert.Equal(1,handler.Calls);
    }
    /// <summary>持久化引用包含全部身份分量，任何改变都不能向新上游查询旧任务。</summary>
    [Theory] [InlineData("server")] [InlineData("model")] [InlineData("credential")] [InlineData("policy")]
    public async Task ChangedBoundTarget_IsRejectedBeforeHttp(string changed)
    {
        using var handler = new StubHttpHandler((_,_) => Task.FromResult(Json(Data().GetProperty("submitted")))); var factory = new StubHttpClientFactory(handler);
        await using var first = Build(factory); var reference = (await first.Video.SubmitAsync(Request(),TestContext.Current.CancellationToken)).Reference;
        var options = changed switch { "server"=>Options(root:"https://other.invalid/api/v3/"), "model"=>Options(model:"other"), "credential"=>Options(key:"other-fixture-key"), _=>Options(responseLimit:1024) };
        await using var next = Build(factory,options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => next.Video.GetAsync(reference,TestContext.Current.CancellationToken)); Assert.Equal(1,handler.Calls);
    }
    /// <summary>调用者修改原生参数不会改变已经开始的实际请求。</summary>
    [Fact]
    public async Task DirectAdapter_SnapshotsOriginalNativeParametersBeforeAwait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? actual = null;
        using var handler = new StubHttpHandler(async (message,token) =>
        { entered.SetResult(); await release.Task.WaitAsync(token); actual = await message.Content!.ReadAsStringAsync(token); return Json(Data().GetProperty("submitted")); });
        var adapter = new VolcengineVideoAdapter(new StubHttpClientFactory(handler),Options()); var request = Request();
        request.Parameters["future"]!["number"] = JsonNode.Parse("9007199254740993");
        var pending = adapter.SubmitAsync(request,TestContext.Current.CancellationToken); await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        request.Parameters.Clear(); release.SetResult(); await pending;
        Assert.Equal("9007199254740993",JsonNode.Parse(actual!)!["future"]!["number"]!.ToJsonString()); Assert.Equal(1,handler.Calls);
    }
    /// <summary>HTTP 工厂只得到本操作的请求；预取消和本地超限不会进入 HTTP。</summary>
    [Fact]
    public async Task LocalRejectionAndCancellation_PerformNoHttp()
    {
        using var handler = new StubHttpHandler((_,_) => throw new InvalidOperationException());
        var adapter = new VolcengineVideoAdapter(new StubHttpClientFactory(handler),Options(requestLimit:10));
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.SubmitAsync(Request(),TestContext.Current.CancellationToken));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetAsync("task_ark_one",cancel.Token)); Assert.Equal(0,handler.Calls);
    }
}
