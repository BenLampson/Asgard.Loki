using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.Vidu.Tests.ViduFixture;

namespace Asgard.Loki.Video.Vidu.Tests;

/// <summary>原生结果结构、任务关联、输入快照与取消释放边界的离线验证。</summary>
public sealed class ViduVideoBoundaryTests
{
    /// <summary>查询只认原生 id，拒绝缺失关联、错误状态和不完整或自相矛盾的成功结果。</summary>
    [Theory] [InlineData("id")] [InlineData("task-id-only")] [InlineData("foreign")] [InlineData("id-number")] [InlineData("id-dot")]
    [InlineData("state")] [InlineData("state-null")] [InlineData("state-bool")] [InlineData("state-empty")]
    [InlineData("creations")] [InlineData("creations-kind")] [InlineData("creations-empty")] [InlineData("creation-kind")]
    [InlineData("creation-id")] [InlineData("creation-id-number")] [InlineData("url")] [InlineData("second-url")]
    [InlineData("err-code")] [InlineData("err-code-null")] [InlineData("code-string")]
    public async Task MalformedQuery_FailsExplicitly(string kind)
    {
        var data = JsonNode.Parse(Query(5).GetRawText())!.AsObject();
        switch (kind)
        {
            case "id": data.Remove("id"); break;
            case "task-id-only": data["task_id"] = data["id"]!.DeepClone(); data.Remove("id"); break;
            case "foreign": data["id"] = "other-task"; break;
            case "id-number": data["id"] = 9007199254740993L; break;
            case "id-dot": data["id"] = "."; break;
            case "state": data.Remove("state"); break;
            case "state-null": data["state"] = null; break;
            case "state-bool": data["state"] = true; break;
            case "state-empty": data["state"] = ""; break;
            case "creations": data.Remove("creations"); break;
            case "creations-kind": data["creations"] = new JsonObject(); break;
            case "creations-empty": data["creations"] = new JsonArray(); break;
            case "creation-kind": data["creations"]![0] = "not-object"; break;
            case "creation-id": data["creations"]![0]!.AsObject().Remove("id"); break;
            case "creation-id-number": data["creations"]![0]!["id"] = 9007199254740993L; break;
            case "url": data["creations"]![0]!["url"] = ""; break;
            case "second-url": data["creations"]![1]!.AsObject().Remove("url"); break;
            case "err-code": data["err_code"] = "ContentModeration"; break;
            case "err-code-null": data["err_code"] = null; break;
            default: data["code"] = "400"; break;
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(data.ToJsonString()))); var adapter = new ViduVideoAdapter(new StubHttpClientFactory(handler), Options());
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetAsync("vidu-fixture-01", TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>提交身份必须是 task_id，不把查询 id 或数字隐式转换为提交确认。</summary>
    [Theory] [InlineData("id-only")] [InlineData("missing")] [InlineData("number")]
    public async Task InvalidAcknowledgement_FailsExplicitly(string kind)
    {
        var body = kind switch { "id-only" => "{\"id\":\"task\",\"state\":\"created\"}", "number" => "{\"task_id\":9007199254740993}", _ => "{\"state\":\"created\"}" };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body))); var adapter = new ViduVideoAdapter(new StubHttpClientFactory(handler), Options());
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.SubmitAsync(Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>未知字段中的无效转义、重复属性、损坏编码及错误媒体类型同样严格失败。</summary>
    [Theory] [InlineData("duplicate")] [InlineData("utf8")] [InlineData("escape")] [InlineData("array")] [InlineData("truncated")] [InlineData("media-type")]
    public async Task InvalidWire_FailsExplicitly(string kind)
    {
        var bytes = kind switch
        {
            "duplicate" => Encoding.UTF8.GetBytes("{\"id\":\"task\",\"id\":\"task\",\"state\":\"processing\"}"), "utf8" => new byte[] { 0xff },
            "escape" => Encoding.UTF8.GetBytes("{\"id\":\"task\",\"state\":\"processing\",\"Future\":\"\\uD800\"}"), "array" => Encoding.UTF8.GetBytes("[]"),
            "truncated" => Encoding.UTF8.GetBytes("{\"id\":"), _ => Encoding.UTF8.GetBytes(Query(2).GetRawText())
        };
        using var handler = new StubHttpHandler((_, _) =>
        {
            var content = new ByteArrayContent(bytes); content.Headers.ContentType = new(kind == "media-type" ? "application/jsonp" : "application/json"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        var adapter = new ViduVideoAdapter(new StubHttpClientFactory(handler), Options()); await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetAsync("task", TestContext.Current.CancellationToken));
    }
    /// <summary>参数在网络等待前深复制，调用方修改媒体、payload 或未知参数不影响原生请求。</summary>
    [Fact]
    public async Task Snapshot_PrecedesNetworkWait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var request = Request(3); var fixture = Case(3);
        using var handler = new StubHttpHandler(async (message, ct) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(ct); using var document = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(ct)); Assert.True(JsonElement.DeepEquals(fixture.GetProperty("wire_request"), document.RootElement)); return Json(fixture.GetProperty("wire_response"));
        });
        var adapter = new ViduVideoAdapter(new StubHttpClientFactory(handler), Options(ViduVideoOperation.ReferenceToVideo, "viduq2")); var pending = adapter.SubmitAsync(request, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken); request.Parameters.Clear(); release.SetResult(); await pending;
    }
    /// <summary>模型覆盖、无效 Unicode、预取消、请求和响应字节上限都在明确边界拒绝。</summary>
    [Fact]
    public async Task InputEncodingLimitsAndCancellation_AreEnforced()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(0)))); var factory = new StubHttpClientFactory(handler);
        var overrideModel = Request(); overrideModel.Parameters["model"] = "another"; await Assert.ThrowsAsync<ArgumentException>(() => new ViduVideoAdapter(factory, Options()).SubmitAsync(overrideModel, TestContext.Current.CancellationToken));
        var invalidText = Request(); invalidText.Parameters["payload"] = "\ud800"; await Assert.ThrowsAsync<EncoderFallbackException>(() => new ViduVideoAdapter(factory, Options()).SubmitAsync(invalidText, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => new ViduVideoAdapter(factory, Options(requestLimit: 1)).SubmitAsync(Request(), TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ViduVideoAdapter(factory, Options()).GetAsync("task", cancellation.Token)); Assert.Equal(0, handler.Calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ViduVideoAdapter(factory, Options(responseLimit: 1)).GetAsync("vidu-fixture-01", TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>跳转路径段和控制字符不能作为不透明任务标识使用。</summary>
    [Theory] [InlineData(".")] [InlineData("..")] [InlineData("a\r\nb")]
    public async Task InvalidTaskId_FailsBeforeIo(string id)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var adapter = new ViduVideoAdapter(new StubHttpClientFactory(handler), Options());
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.GetAsync(id, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>成功结果不能越过最终客户端释放故障或取消；工厂仍由应用拥有。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FinalDisposal_PrecedesSuccessfulDelivery(bool cancel)
    {
        using var cancellation = new CancellationTokenSource(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(5))));
        using var factory = new ViduTestHttpFactory(handler) { OnDispose = () => { if (cancel) cancellation.Cancel(); else throw new IOException("fixture disposal"); } }; var adapter = new ViduVideoAdapter(factory, Options());
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetAsync("vidu-fixture-01", cancellation.Token)); else await Assert.ThrowsAsync<IOException>(() => adapter.GetAsync("vidu-fixture-01", cancellation.Token));
        Assert.Equal(1, factory.ClientsDisposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>创建客户端期间取消也在发送前失败并释放已创建客户端，不处置借用工厂。</summary>
    [Fact]
    public async Task CancellationDuringClientCreation_FailsBeforeSend()
    {
        using var cancellation = new CancellationTokenSource(); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new ViduTestHttpFactory(handler) { OnCreate = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ViduVideoAdapter(factory, Options()).GetAsync("task", cancellation.Token)); Assert.Equal(0, handler.Calls); Assert.Equal(1, factory.ClientsDisposed); Assert.False(factory.Disposed);
    }
    /// <summary>配置不能泄露密钥，模块正常关闭不处置借用工厂。</summary>
    [Fact]
    public async Task CredentialMetadataAndBorrowedLifetime_ArePreserved()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Case(0).GetProperty("wire_response")))); using var factory = new ViduTestHttpFactory(handler); var options = Options(); var adapter = new ViduVideoAdapter(factory, options);
        Assert.DoesNotContain(options.ApiKey, JsonSerializer.Serialize(options)); Assert.DoesNotContain(options.ApiKey, options.ToString()!); Assert.DoesNotContain(options.ApiKey, adapter.BindingIdentity);
        var loki = Build(factory); await loki.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken); await loki.DisposeAsync(); Assert.False(factory.Disposed); Assert.Equal(1, factory.ClientsDisposed);
    }
    /// <summary>无效操作、地址、凭据或字节上限在构造时拒绝。</summary>
    [Theory] [InlineData("operation")] [InlineData("root")] [InlineData("query")] [InlineData("userinfo")]
    [InlineData("key")] [InlineData("empty-key")] [InlineData("request-limit")] [InlineData("response-limit")]
    public void InvalidTarget_IsRejected(string kind)
    {
        var options = kind switch
        {
            "operation" => Options((ViduVideoOperation)0), "root" => Options(root: "http://vidu.invalid/"), "query" => Options(root: "https://vidu.invalid/?other=1"),
            "userinfo" => Options(root: "https://user@vidu.invalid/"), "key" => Options(key: "bad\r\nkey"), "empty-key" => Options(key: ""), "request-limit" => Options(requestLimit: 0), _ => Options(responseLimit: 0)
        };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); Assert.ThrowsAny<ArgumentException>(() => new ViduVideoAdapter(new StubHttpClientFactory(handler), options)); Assert.Equal(0, handler.Calls);
    }
}
