using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.Kling.Tests.KlingFixture;

namespace Asgard.Loki.Video.Kling.Tests;

/// <summary>可灵原生结果完整性、输入快照、资源边界及最终取消验证。</summary>
public sealed class KlingVideoBoundaryTests
{
    /// <summary>外层代码、任务关联、状态和完整成功结果均必须有效，不能把缺失数据当作成功。</summary>
    [Theory] [InlineData("code")] [InlineData("code-string")] [InlineData("data")] [InlineData("id")] [InlineData("id-number")]
    [InlineData("foreign")] [InlineData("status")] [InlineData("status-kind")] [InlineData("result")] [InlineData("videos")]
    [InlineData("empty-videos")] [InlineData("url")] [InlineData("second-url")]
    public async Task InvalidNativeQuery_FailsExplicitly(string kind)
    {
        var data = JsonNode.Parse(Query(4).GetRawText())!.AsObject(); var task = data["data"]!.AsObject();
        switch (kind)
        {
            case "code": data.Remove("code"); break;
            case "code-string": data["code"] = "0"; break;
            case "data": data["data"] = null; break;
            case "id": task.Remove("task_id"); break;
            case "id-number": task["task_id"] = 9007199254740993L; break;
            case "foreign": task["task_id"] = "other-task"; break;
            case "status": task.Remove("task_status"); break;
            case "status-kind": task["task_status"] = true; break;
            case "result": task.Remove("task_result"); break;
            case "videos": task["task_result"]!["videos"] = new JsonObject(); break;
            case "empty-videos": task["task_result"]!["videos"] = new JsonArray(); break;
            case "url": task["task_result"]!["videos"]![0]!["url"] = ""; break;
            default: task["task_result"]!["videos"]![1]!.AsObject().Remove("url"); break;
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(data.ToJsonString()))); var adapter = new KlingVideoAdapter(new StubHttpClientFactory(handler), Options(), new KlingTestClock());
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetAsync("kling-fixture-01", TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>重复属性、非法 UTF-8、未知成员的损坏字符串及错误媒体类型均不能静默接受。</summary>
    [Theory] [InlineData("duplicate")] [InlineData("utf8")] [InlineData("escape")] [InlineData("array")] [InlineData("truncated")] [InlineData("media-type")]
    public async Task InvalidWireBytes_FailExplicitly(string kind)
    {
        var bytes = kind switch
        {
            "duplicate" => Encoding.UTF8.GetBytes("{\"code\":0,\"code\":0}"), "utf8" => new byte[] { 0xff },
            "escape" => Encoding.UTF8.GetBytes("{\"code\":0,\"data\":{\"task_id\":\"task\"},\"Future\":\"\\uD800\"}"),
            "array" => Encoding.UTF8.GetBytes("[]"), "truncated" => Encoding.UTF8.GetBytes("{\"code\":"), _ => Encoding.UTF8.GetBytes(Query(0).GetRawText())
        };
        using var handler = new StubHttpHandler((_, _) =>
        {
            var content = new ByteArrayContent(bytes); content.Headers.ContentType = new(kind == "media-type" ? "application/jsonp" : "application/json"); return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        });
        var adapter = new KlingVideoAdapter(new StubHttpClientFactory(handler), Options(), new KlingTestClock()); await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetAsync("task", TestContext.Current.CancellationToken));
    }
    /// <summary>两种模型字段均由完整目标保留，调用参数不能覆盖或触发旧接口默认模型。</summary>
    [Theory] [InlineData("model")] [InlineData("model_name")]
    public async Task ReservedModelFields_FailBeforeIo(string name)
    {
        var request = Request(); request.Parameters[name] = "other-model"; using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        var adapter = new KlingVideoAdapter(new StubHttpClientFactory(handler), Options(), new KlingTestClock()); await Assert.ThrowsAsync<ArgumentException>(() => adapter.SubmitAsync(request, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>任务 ID 不能被 URI 路径规范化改变目标，不触发 HTTP。</summary>
    [Theory] [InlineData(".")] [InlineData("..")]
    public async Task InvalidTaskPath_FailsBeforeIo(string id)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var adapter = new KlingVideoAdapter(new StubHttpClientFactory(handler), Options(), new KlingTestClock());
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.GetAsync(id, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>请求在首个网络等待前完整复制，原始字符串时长、大整数和媒体不会被后续修改影响。</summary>
    [Fact]
    public async Task Snapshot_PrecedesTransportWait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var request = Request();
        using var handler = new StubHttpHandler(async (message, ct) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(ct); using var document = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(ct)); Assert.True(JsonElement.DeepEquals(Data().GetProperty("submissions")[0].GetProperty("wire_request"), document.RootElement)); return Json(Data().GetProperty("submit_response"));
        });
        var adapter = new KlingVideoAdapter(new StubHttpClientFactory(handler), Options(), new KlingTestClock()); var pending = adapter.SubmitAsync(request, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken); request.Parameters.Clear(); release.SetResult(); await pending;
    }
    /// <summary>生成请求和响应均应用显式原始字节限制，预取消和无效 Unicode 在 IO 前失败。</summary>
    [Fact]
    public async Task LimitsEncodingAndCancellation_AreEnforced()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(0)))); var factory = new StubHttpClientFactory(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new KlingVideoAdapter(factory, Options(requestLimit: 1), new KlingTestClock()).SubmitAsync(Request(), TestContext.Current.CancellationToken));
        var request = Request(); request.Parameters["Future"] = "\ud800"; await Assert.ThrowsAsync<EncoderFallbackException>(() => new KlingVideoAdapter(factory, Options(), new KlingTestClock()).SubmitAsync(request, TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new KlingVideoAdapter(factory, Options(), new KlingTestClock()).GetAsync("task", cancellation.Token)); Assert.Equal(0, handler.Calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => new KlingVideoAdapter(factory, Options(responseLimit: 1), new KlingTestClock()).GetAsync("task", TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>客户端最终释放故障或取消不能被已经读取的成功任务结果掩盖。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FinalDisposalFaultOrCancellation_PreventsResult(bool cancel)
    {
        using var cancellation = new CancellationTokenSource(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(4))));
        using var factory = new KlingTestHttpFactory(handler) { OnDispose = () => { if (cancel) cancellation.Cancel(); else throw new IOException("fixture disposal"); } }; var adapter = new KlingVideoAdapter(factory, Options(), new KlingTestClock());
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetAsync("kling-fixture-01", cancellation.Token)); else await Assert.ThrowsAsync<IOException>(() => adapter.GetAsync("kling-fixture-01", cancellation.Token));
        Assert.Equal(1, handler.Calls); Assert.False(factory.Disposed);
    }
    /// <summary>无效操作、认证、资源边界、凭据混用或认证头都在构造时拒绝。</summary>
    [Theory] [InlineData("operation")] [InlineData("authentication")] [InlineData("root")] [InlineData("request-limit")]
    [InlineData("api-key")] [InlineData("jwt-key")]
    public void InvalidOptions_AreRejected(string kind)
    {
        var options = kind switch
        {
            "operation" => Options(operation: (KlingVideoOperation)0), "authentication" => Options(authentication: (KlingVideoAuthentication)0),
            "root" => Options(root: "http://kling.invalid/"), "request-limit" => Options(requestLimit: 0),
            "api-key" => Options(authentication: KlingVideoAuthentication.ApiKey, apiKey: "secret\r\nkey"), _ => Options(secret: "secret\r\nkey")
        };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); Assert.ThrowsAny<ArgumentException>(() => new KlingVideoAdapter(new StubHttpClientFactory(handler), options)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>显式认证模式不接受两种凭据同时存在，也不因未使用的空值产生另一身份。</summary>
    [Fact]
    public void MixedCredentials_FailAndUnusedNullsDoNotChangeIdentity()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new StubHttpClientFactory(handler);
        Assert.Throws<ArgumentException>(() => new KlingVideoAdapter(factory, new() { Model = "kling-v2-6", Operation = KlingVideoOperation.TextToVideo, Authentication = KlingVideoAuthentication.ApiKey, ApiKey = "fixture-api-key", AccessKeyId = "unselected-access", SecretAccessKey = "unselected-secret" }));
        var original = Options(authentication: KlingVideoAuthentication.ApiKey);
        var same = new KlingVideoOptions { Id = original.Id, Model = original.Model, Operation = original.Operation, Authentication = original.Authentication, ApiKey = original.ApiKey,
            BaseAddress = original.BaseAddress, HttpClientName = original.HttpClientName, AccessKeyId = null!, SecretAccessKey = null! };
        Assert.Equal(new KlingVideoAdapter(factory, original).BindingIdentity, new KlingVideoAdapter(factory, same).BindingIdentity);
    }
}
