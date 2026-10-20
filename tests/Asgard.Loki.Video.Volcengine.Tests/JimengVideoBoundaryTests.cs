using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.Volcengine.Tests.JimengFixture;

namespace Asgard.Loki.Video.Volcengine.Tests;

/// <summary>Visual 原生任务关联、成功条件、字节/编码边界与最终资源释放测试。</summary>
public sealed class JimengVideoBoundaryTests
{
    /// <summary>提交必须返回原始字符串任务 ID，不能缺省、强制字符串化或经过浮点数。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MissingOrNonStringSubmitId_FailsExplicitly(bool numeric)
    {
        var data = JsonNode.Parse(Data().GetProperty("submit_response").GetRawText())!;
        if (numeric) data["data"]!["task_id"] = 9007199254740993L; else data["data"]!.AsObject().Remove("task_id");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ArkFixture.Json(data.ToJsonString()))); var adapter = new JimengVideoAdapter(new StubHttpClientFactory(handler), Options());
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.SubmitAsync(Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>冗余顶层 status 原样保留但不用于状态判断；原生业务结果由 code 和 data.status 决定。</summary>
    [Fact]
    public async Task RedundantRootStatus_DoesNotOverrideNativeTaskState()
    {
        var data = JsonNode.Parse(Query(2).GetRawText())!; data["status"] = 50413;
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ArkFixture.Json(data.ToJsonString()))); var adapter = new JimengVideoAdapter(new StubHttpClientFactory(handler), Options());
        var result = await adapter.GetAsync("jimeng-fixture-01", TestContext.Current.CancellationToken); Assert.Equal(VideoTaskState.Succeeded, result.State); Assert.Equal(50413, result.Data.GetProperty("status").GetInt32());
    }
    /// <summary>缺失正文、状态、完成地址，或显式回传的另一任务/服务，均不能成为可用结果。</summary>
    [Theory] [InlineData("data")] [InlineData("status")] [InlineData("url")] [InlineData("id")] [InlineData("req_key")]
    [InlineData("code")] [InlineData("code-string")]
    public async Task InvalidNativeResult_FailsExplicitly(string kind)
    {
        var data = JsonNode.Parse(Query(2).GetRawText())!.AsObject();
        if (kind == "data") data["data"] = null; else if (kind == "status") data["data"]!["status"] = false;
        else if (kind == "url") data["data"]!["video_url"] = ""; else if (kind == "id") data["data"]!["task_id"] = "other-task";
        else if (kind == "req_key") data["data"]!["req_key"] = "other_req_key"; else if (kind == "code") data.Remove("code"); else data["code"] = "10000";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ArkFixture.Json(data.ToJsonString()))); var adapter = new JimengVideoAdapter(new StubHttpClientFactory(handler), Options());
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetAsync("jimeng-fixture-01", TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>done 和错误外层 code 冲突时仍完整抛出原生错误，不能返回成功。</summary>
    [Fact]
    public async Task DoneWithNonSuccessCode_DoesNotBecomeSuccess()
    {
        var data = JsonNode.Parse(Query(2).GetRawText())!; data["code"] = 50413;
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ArkFixture.Json(data.ToJsonString()))); var adapter = new JimengVideoAdapter(new StubHttpClientFactory(handler), Options());
        var error = await Assert.ThrowsAsync<VideoUpstreamException>(() => adapter.GetAsync("jimeng-fixture-01", TestContext.Current.CancellationToken));
        Assert.Equal(data.ToJsonString(), Encoding.UTF8.GetString(error.ResponseBody.Span)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>可选任务/服务回传与本次请求相符时保留，不向其他无回传结果注入这些字段。</summary>
    [Fact]
    public async Task MatchingOptionalEcho_IsPreserved()
    {
        var data = JsonNode.Parse(Query(2).GetRawText())!; data["data"]!["task_id"] = "jimeng-fixture-01"; data["data"]!["req_key"] = "fixture_req_key";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ArkFixture.Json(data.ToJsonString()))); var adapter = new JimengVideoAdapter(new StubHttpClientFactory(handler), Options());
        var result = await adapter.GetAsync("jimeng-fixture-01", TestContext.Current.CancellationToken); Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(data), result.Data));
    }
    /// <summary>重复属性、损坏 Unicode、截断或非 JSON 媒体类型不能被宽松读取修复。</summary>
    [Theory] [InlineData("duplicate")] [InlineData("utf8")] [InlineData("escape")] [InlineData("array")] [InlineData("truncated")] [InlineData("media-type")]
    public async Task InvalidWireBytes_FailExplicitly(string kind)
    {
        var bytes = kind switch
        {
            "duplicate" => Encoding.UTF8.GetBytes("{\"code\":10000,\"code\":10000}"), "utf8" => new byte[] { 0xff },
            "escape" => Encoding.UTF8.GetBytes("{\"code\":10000,\"data\":{\"task_id\":\"task\"},\"Future\":\"\\uD800\"}"),
            "array" => Encoding.UTF8.GetBytes("[]"), "truncated" => Encoding.UTF8.GetBytes("{\"code\":"), _ => Encoding.UTF8.GetBytes(Data().GetProperty("submit_response").GetRawText())
        };
        using var handler = new StubHttpHandler((_, _) =>
        {
            var content = new ByteArrayContent(bytes); content.Headers.ContentType = new(kind == "media-type" ? "text/html" : "application/json"); return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        });
        var adapter = new JimengVideoAdapter(new StubHttpClientFactory(handler), Options()); await Assert.ThrowsAsync<InvalidDataException>(() => adapter.SubmitAsync(Request(), TestContext.Current.CancellationToken));
    }
    /// <summary>原生 req_key 不能覆盖完整目标，也不能由输入图片数量隐式改变。</summary>
    [Fact]
    public async Task ReservedSubmitRequestKey_FailsBeforeIo()
    {
        var request = Request(); request.Parameters["req_key"] = "other_req_key";
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var adapter = new JimengVideoAdapter(new StubHttpClientFactory(handler), Options());
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.SubmitAsync(request, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>原生查询配置不覆盖服务标识或任务关联，req_json 也不能被强制转换为对象。</summary>
    [Theory] [InlineData("req_key")] [InlineData("task_id")] [InlineData("req_json")]
    public async Task InvalidQueryParameters_FailBeforeIo(string name)
    {
        var parameters = new JsonObject { [name] = new JsonObject() }; using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var adapter = new JimengVideoAdapter(new StubHttpClientFactory(handler), Options());
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.GetAsync("task", parameters, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>输入复制在提交网络等待前完成，不改变签名正文或未知原生字段。</summary>
    [Fact]
    public async Task SubmitSnapshot_PrecedesWait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var request = Request();
        using var handler = new StubHttpHandler(async (message, ct) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(ct); var native = JsonNode.Parse(await message.Content!.ReadAsStringAsync(ct))!;
            Assert.Equal("A fictional paper boat on a lake. 紙船🌊", native["prompt"]!.GetValue<string>()); Assert.Equal(9007199254740993L, native["Future"]!["large"]!.GetValue<long>()); return ArkFixture.Json(Data().GetProperty("submit_response"));
        });
        var adapter = new JimengVideoAdapter(new StubHttpClientFactory(handler), Options()); var pending = adapter.SubmitAsync(request, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken); request.Parameters.Clear(); release.SetResult(); await pending;
    }
    /// <summary>正文限制、非法 Unicode 与预取消在 IO 前失败；响应限制应用于实际原始字节。</summary>
    [Fact]
    public async Task LimitsEncodingAndCancellation_AreExplicit()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ArkFixture.Json(Query(2)))); var factory = new StubHttpClientFactory(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new JimengVideoAdapter(factory, Options(requestLimit: 1)).SubmitAsync(Request(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => new JimengVideoAdapter(factory, Options(requestLimit: 1)).GetAsync("task", TestContext.Current.CancellationToken));
        var input = new JsonObject { ["Future"] = "\ud800" }; await Assert.ThrowsAsync<EncoderFallbackException>(() => new JimengVideoAdapter(factory, Options()).GetAsync("task", input, TestContext.Current.CancellationToken));
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new JimengVideoAdapter(factory, Options()).GetAsync("task", cancel.Token)); Assert.Equal(0, handler.Calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => new JimengVideoAdapter(factory, Options(responseLimit: 1)).GetAsync("task", TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>完整结果已经到达后，最后资源释放或取消仍会阻止交付，工厂不由 SDK 释放。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FinalDisposalFaultOrCancellation_PreventsResult(bool cancel)
    {
        using var cancellation = new CancellationTokenSource(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(ArkFixture.Json(Query(2))));
        using var factory = new VideoHttpFactory(handler) { OnClientDispose = () => { if (cancel) cancellation.Cancel(); else throw new IOException("fixture dispose"); } }; var adapter = new JimengVideoAdapter(factory, Options());
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetAsync("task", cancellation.Token)); else await Assert.ThrowsAsync<IOException>(() => adapter.GetAsync("task", TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls); Assert.Equal(0, factory.Disposals);
    }
    /// <summary>捕获的凭据不进入公开配置或身份摘要，认证语法和根路径在构造时严格验证。</summary>
    [Fact]
    public void CredentialsAndOptions_StayExplicit()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new StubHttpClientFactory(handler); var options = Options(token: "fixture-token"); var adapter = new JimengVideoAdapter(factory, options);
        foreach (var secret in new[] { options.AccessKeyId, options.SecretAccessKey, options.SecurityToken! }) { Assert.DoesNotContain(secret, JsonSerializer.Serialize(options)); Assert.DoesNotContain(secret, adapter.BindingIdentity); Assert.DoesNotContain(secret, options.ToString()!); }
        Assert.Throws<ArgumentException>(() => new JimengVideoAdapter(factory, Options(access: "invalid/access"))); Assert.Throws<ArgumentException>(() => new JimengVideoAdapter(factory, Options(token: "secret\r\ntoken")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JimengVideoAdapter(factory, Options(requestLimit: 0))); Assert.Equal(0, handler.Calls);
    }
}
