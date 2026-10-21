using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.XAI.Tests.XaiFixture;

namespace Asgard.Loki.Video.XAI.Tests;

/// <summary>请求身份、审核标记、HTTP 空正文和最终资源边界验证。</summary>
public sealed class XaiVideoBoundaryTests
{
    /// <summary>可选原生回显不能隐藏关联错误；已知完成必须有有效结果和明确布尔审核标记。</summary>
    [Theory] [InlineData("status")] [InlineData("status-null")] [InlineData("status-number")] [InlineData("status-empty")]
    [InlineData("echo-foreign")] [InlineData("echo-null")] [InlineData("echo-number")]
    [InlineData("video")] [InlineData("video-null")] [InlineData("video-array")] [InlineData("moderation-null")] [InlineData("moderation-string")]
    [InlineData("url")] [InlineData("url-empty")] [InlineData("url-number")] [InlineData("success-error")] [InlineData("pending-error")]
    public async Task InvalidNativeQuery_FailsExplicitly(string kind)
    {
        var body = JsonNode.Parse(Query(3).GetProperty("body").GetRawText())!.AsObject();
        switch (kind)
        {
            case "status": body.Remove("status"); break;
            case "status-null": body["status"] = null; break;
            case "status-number": body["status"] = 100; break;
            case "status-empty": body["status"] = ""; break;
            case "echo-foreign": body["request_id"] = "another-task"; break;
            case "echo-null": body["request_id"] = null; break;
            case "echo-number": body["request_id"] = 9007199254740993L; break;
            case "video": body.Remove("video"); break;
            case "video-null": body["video"] = null; break;
            case "video-array": body["video"] = new JsonArray(); break;
            case "moderation-null": body["video"]!["respect_moderation"] = null; break;
            case "moderation-string": body["video"]!["respect_moderation"] = "false"; break;
            case "url": body["video"]!.AsObject().Remove("url"); break;
            case "url-empty": body["video"]!["url"] = ""; break;
            case "url-number": body["video"]!["url"] = 123; break;
            default: if (kind == "pending-error") body["status"] = "pending"; body["error"] = new JsonObject { ["code"] = "invalid_argument" }; break;
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body.ToJsonString()))); await Assert.ThrowsAsync<InvalidDataException>(() => new XaiVideoAdapter(new StubHttpClientFactory(handler), Options()).GetAsync("xai-fixture-01", TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>提交必须明确提供原生字符串 request_id，不能借用查询上下文或数字。</summary>
    [Theory] [InlineData("missing")] [InlineData("null")] [InlineData("number")] [InlineData("blank")] [InlineData("dot")]
    public async Task InvalidAcknowledgement_IsRejected(string kind)
    {
        var body = new JsonObject(); if (kind != "missing") body["request_id"] = kind switch { "null" => null, "number" => JsonValue.Create(9007199254740993L), "blank" => JsonValue.Create(" "), _ => JsonValue.Create(".") };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body.ToJsonString()))); await Assert.ThrowsAsync<InvalidDataException>(() => new XaiVideoAdapter(new StubHttpClientFactory(handler), Options()).SubmitAsync(Request(), TestContext.Current.CancellationToken));
    }
    /// <summary>HTTP 202 仍表示未完成，不能同时宣告 done、failed 或 expired。</summary>
    [Theory] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(7)]
    public async Task AcceptedTerminal_IsContradictory(int index)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(index).GetProperty("body"), HttpStatusCode.Accepted)));
        await Assert.ThrowsAsync<InvalidDataException>(() => new XaiVideoAdapter(new StubHttpClientFactory(handler), Options()).GetAsync("task", TestContext.Current.CancellationToken));
    }
    /// <summary>只有查询的真正空正文 202 可以没有 JSON，其他空/非对象/损坏正文严格失败。</summary>
    [Theory] [InlineData("empty-200")] [InlineData("empty-204")] [InlineData("empty-submit")]
    [InlineData("null-202")] [InlineData("array-202")] [InlineData("space-202")] [InlineData("duplicate")]
    [InlineData("utf8")] [InlineData("escape")] [InlineData("truncated")] [InlineData("media-type")]
    public async Task InvalidRawResponse_CannotBecomePendingOrSuccessful(string kind)
    {
        var bytes = kind switch
        {
            "empty-200" or "empty-204" or "empty-submit" => Array.Empty<byte>(), "null-202" => "null"u8.ToArray(), "array-202" => "[]"u8.ToArray(), "space-202" => " "u8.ToArray(),
            "duplicate" => "{\"status\":\"pending\",\"status\":\"done\"}"u8.ToArray(), "utf8" => new byte[] { 0xff },
            "escape" => "{\"status\":\"pending\",\"Future\":\"\\uD800\"}"u8.ToArray(), "truncated" => "{\"status\":"u8.ToArray(), _ => Encoding.UTF8.GetBytes(Query(2).GetProperty("body").GetRawText())
        };
        var status = kind == "empty-204" ? HttpStatusCode.NoContent : kind.EndsWith("202", StringComparison.Ordinal) || kind == "empty-submit" ? HttpStatusCode.Accepted : HttpStatusCode.OK;
        using var handler = new StubHttpHandler((_, _) =>
        { var content = new ByteArrayContent(bytes); content.Headers.ContentType = new(kind == "media-type" ? "application/jsonp" : "application/json"); return Task.FromResult(new HttpResponseMessage(status) { Content = content }); });
        var adapter = new XaiVideoAdapter(new StubHttpClientFactory(handler), Options()); await Assert.ThrowsAsync<InvalidDataException>(() => kind == "empty-submit" ? adapter.SubmitAsync(Request(), TestContext.Current.CancellationToken) : adapter.GetAsync("task", TestContext.Current.CancellationToken));
    }
    /// <summary>空正文 202 也必须完整验证声明长度，不得绕过真实 EOF 检查。</summary>
    [Fact]
    public async Task EmptyAcceptedResponse_StillChecksDeclaredLength()
    {
        using var handler = new StubHttpHandler((_, _) => { var content = new ByteArrayContent([]); content.Headers.ContentLength = 1; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted) { Content = content }); });
        await Assert.ThrowsAsync<InvalidDataException>(() => new XaiVideoAdapter(new StubHttpClientFactory(handler), Options()).GetAsync("task", TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>等待前深复制全部原始参数，调用方后续修改帧或未知成员不会影响请求。</summary>
    [Fact]
    public async Task Snapshot_PrecedesTransportWait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var request = Request(3);
        using var handler = new StubHttpHandler(async (message, ct) =>
        { entered.SetResult(); await release.Task.WaitAsync(ct); using var document = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(ct)); Assert.True(JsonElement.DeepEquals(Case(3).GetProperty("wire_request"), document.RootElement)); return Json(Case(3).GetProperty("wire_response")); });
        var pending = new XaiVideoAdapter(new StubHttpClientFactory(handler), Options(model: "grok-imagine-video-1.5")).SubmitAsync(request, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken); request.Parameters.Clear(); release.SetResult(); await pending;
    }
    /// <summary>模型覆盖、无效 Unicode、预取消和请求/响应原始字节限制明确执行。</summary>
    [Fact]
    public async Task InputLimitsEncodingAndCancellation_AreEnforced()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(QueryResponse(3))); var factory = new StubHttpClientFactory(handler); var model = Request(); model.Parameters["model"] = "another";
        await Assert.ThrowsAsync<ArgumentException>(() => new XaiVideoAdapter(factory, Options()).SubmitAsync(model, TestContext.Current.CancellationToken)); var invalid = Request(); invalid.Parameters["Future"] = "\ud800";
        await Assert.ThrowsAsync<EncoderFallbackException>(() => new XaiVideoAdapter(factory, Options()).SubmitAsync(invalid, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => new XaiVideoAdapter(factory, Options(requestLimit: 1)).SubmitAsync(Request(), TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new XaiVideoAdapter(factory, Options()).GetAsync("task", cancellation.Token)); Assert.Equal(0, handler.Calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => new XaiVideoAdapter(factory, Options(responseLimit: 1)).GetAsync("task", TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>成功 JSON 和空正文运行中结果都不能越过最终释放故障或取消。</summary>
    [Theory] [InlineData(0, false)] [InlineData(0, true)] [InlineData(3, false)] [InlineData(3, true)]
    public async Task DisposalAndFinalCancellation_PrecedeDelivery(int index, bool cancel)
    {
        using var cancellation = new CancellationTokenSource(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(QueryResponse(index))); using var factory = new XaiTestHttpFactory(handler) { OnDispose = () => { if (cancel) cancellation.Cancel(); else throw new IOException("fixture disposal"); } };
        var adapter = new XaiVideoAdapter(factory, Options()); if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetAsync("task", cancellation.Token)); else await Assert.ThrowsAsync<IOException>(() => adapter.GetAsync("task", cancellation.Token)); Assert.False(factory.Disposed); Assert.Equal(1, factory.ClientsDisposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>创建客户端期间取消不发送请求，且释放本次客户端，借用工厂不受影响。</summary>
    [Fact]
    public async Task ClientCreationCancellation_FailsBeforeSend()
    {
        using var cancellation = new CancellationTokenSource(); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new XaiTestHttpFactory(handler) { OnCreate = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new XaiVideoAdapter(factory, Options()).GetAsync("task", cancellation.Token)); Assert.Equal(0, handler.Calls); Assert.Equal(1, factory.ClientsDisposed); Assert.False(factory.Disposed);
    }
    /// <summary>凭据不进入配置 JSON/显示/身份摘要，正常模块关闭不处置借用工厂。</summary>
    [Fact]
    public async Task SecretsAndBorrowedLifetime_ArePreserved()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Case(0).GetProperty("wire_response")))); using var factory = new XaiTestHttpFactory(handler); var options = Options(); var adapter = new XaiVideoAdapter(factory, options);
        Assert.DoesNotContain(options.ApiKey, JsonSerializer.Serialize(options)); Assert.DoesNotContain(options.ApiKey, options.ToString()!); Assert.DoesNotContain(options.ApiKey, adapter.BindingIdentity);
        var loki = Build(factory); await loki.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken); await loki.DisposeAsync(); Assert.False(factory.Disposed); Assert.Equal(1, factory.ClientsDisposed);
    }
    /// <summary>危险路径段与控制字符在查询前拒绝，不发送 HTTP。</summary>
    [Theory] [InlineData(".")] [InlineData("..")] [InlineData("a\r\nb")]
    public async Task InvalidTaskPath_FailsBeforeIo(string id)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); await Assert.ThrowsAsync<ArgumentException>(() => new XaiVideoAdapter(new StubHttpClientFactory(handler), Options()).GetAsync(id, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>无效操作、根地址、认证头、缺失凭据和字节上限在构造时拒绝。</summary>
    [Theory] [InlineData("operation")] [InlineData("root")] [InlineData("key")] [InlineData("empty-key")]
    [InlineData("request-limit")] [InlineData("response-limit")]
    public void InvalidConfiguration_IsRejected(string kind)
    {
        var options = kind switch { "operation" => Options((XaiVideoOperation)0), "root" => Options(root: "http://xai.invalid/"), "key" => Options(key: "bad\r\nkey"), "empty-key" => Options(key: ""), "request-limit" => Options(requestLimit: 0), _ => Options(responseLimit: 0) };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); Assert.ThrowsAny<ArgumentException>(() => new XaiVideoAdapter(new StubHttpClientFactory(handler), options)); Assert.Equal(0, handler.Calls);
    }
}
