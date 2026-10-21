using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.Midjourney.Tests.MidjourneyFixture;

namespace Asgard.Loki.Video.Midjourney.Tests;

/// <summary>代理任务身份、视频结果完整性、参数快照及资源边界验证。</summary>
public sealed class MidjourneyVideoBoundaryTests
{
    /// <summary>关联错误、图片任务、无效状态和任意损坏的视频输出项均不能成为成功视频。</summary>
    [Theory] [InlineData("id")] [InlineData("id-number")] [InlineData("foreign")] [InlineData("action-image")] [InlineData("action-null")]
    [InlineData("status")] [InlineData("status-null")] [InlineData("status-empty")] [InlineData("status-number")]
    [InlineData("no-output")] [InlineData("list-kind")] [InlineData("list-item")] [InlineData("second-url")]
    [InlineData("flat-kind")] [InlineData("image-kind")] [InlineData("fail-reason")] [InlineData("legacy-no-action")]
    public async Task InvalidNativeQuery_FailsExplicitly(string kind)
    {
        var body = JsonNode.Parse(Query(5).GetRawText())!.AsObject();
        switch (kind)
        {
            case "id": body.Remove("id"); break;
            case "id-number": body["id"] = 9007199254740993L; break;
            case "foreign": body["id"] = "another-task"; break;
            case "action-image": body["action"] = "IMAGINE"; break;
            case "action-null": body["action"] = null; break;
            case "status": body.Remove("status"); break;
            case "status-null": body["status"] = null; break;
            case "status-empty": body["status"] = ""; break;
            case "status-number": body["status"] = 5; break;
            case "no-output": body["videoUrl"] = null; body["videoUrls"] = new JsonArray(); body["imageUrl"] = null; break;
            case "list-kind": body["videoUrls"] = new JsonObject(); break;
            case "list-item": body["videoUrls"]![1] = "not-object"; break;
            case "second-url": body["videoUrls"]![1]!.AsObject().Remove("url"); break;
            case "flat-kind": body["videoUrl"] = 123; break;
            case "image-kind": body["imageUrl"] = false; break;
            case "fail-reason": body["failReason"] = "fictional failed output"; break;
            default: body.Remove("action"); body["videoUrl"] = null; body["videoUrls"] = null; break;
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body.ToJsonString())));
        await Assert.ThrowsAsync<InvalidDataException>(() => new MidjourneyVideoAdapter(new StubHttpClientFactory(handler), Options()).GetAsync("mj-video-9007199254740993", TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>提交 code、原始任务 ID 和即时状态必须具有正确原生类型和完整结果。</summary>
    [Theory] [InlineData("code")] [InlineData("code-string")] [InlineData("result")] [InlineData("result-number")]
    [InlineData("result-dot")] [InlineData("properties-kind")] [InlineData("status-null")] [InlineData("instant-no-video")]
    public async Task MalformedAcknowledgement_IsRejected(string kind)
    {
        var body = JsonNode.Parse(Ack(2).GetRawText())!.AsObject();
        switch (kind)
        {
            case "code": body.Remove("code"); break;
            case "code-string": body["code"] = "21"; break;
            case "result": body.Remove("result"); break;
            case "result-number": body["result"] = 9007199254740993L; break;
            case "result-dot": body["result"] = "."; break;
            case "properties-kind": body["properties"] = new JsonArray(); break;
            case "status-null": body["properties"]!["status"] = null; break;
            default: body["properties"]!.AsObject().Remove("videoUrl"); body["properties"]!.AsObject().Remove("videoUrls"); break;
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body.ToJsonString()))); await Assert.ThrowsAsync<InvalidDataException>(() => new MidjourneyVideoAdapter(new StubHttpClientFactory(handler), Options()).SubmitAsync(Request(), TestContext.Current.CancellationToken));
    }
    /// <summary>接受确认可以没有 properties，不擅自补造原生状态。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MissingOrNullProperties_RemainSubmitted(bool explicitNull)
    {
        var body = JsonNode.Parse(Ack().GetRawText())!.AsObject(); if (explicitNull) body["properties"] = null; else body.Remove("properties");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body.ToJsonString()))); var result = await new MidjourneyVideoAdapter(new StubHttpClientFactory(handler), Options()).SubmitAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(VideoTaskState.Submitted, result.State); Assert.Null(result.NativeStatus); Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(result.Data.GetRawText())));
    }
    /// <summary>固定视频类型拒绝大小写别名，不能利用上游不区分大小写的字段绑定覆盖目标。</summary>
    [Theory] [InlineData("videoType")] [InlineData("VideoType")] [InlineData("VIDEOTYPE")] [InlineData("vIdEoTyPe")] [InlineData("model")]
    public async Task ReservedFields_FailBeforeIo(string field)
    {
        var request = Request(); request.Parameters[field] = "other-type"; using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAsync<ArgumentException>(() => new MidjourneyVideoAdapter(new StubHttpClientFactory(handler), Options()).SubmitAsync(request, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>响应为 null、重复属性、无效字符串或错误媒体类型时明确失败，不生成虚构任务。</summary>
    [Theory] [InlineData("null")] [InlineData("array")] [InlineData("duplicate")] [InlineData("utf8")]
    [InlineData("escape")] [InlineData("truncated")] [InlineData("media-type")]
    public async Task MalformedOriginalWire_IsRejected(string kind)
    {
        var bytes = kind switch
        {
            "null" => "null"u8.ToArray(), "array" => "[]"u8.ToArray(), "duplicate" => "{\"id\":\"a\",\"id\":\"b\"}"u8.ToArray(), "utf8" => new byte[] { 0xff },
            "escape" => "{\"id\":\"task\",\"status\":\"IN_PROGRESS\",\"Future\":\"\\uD800\"}"u8.ToArray(), "truncated" => "{\"id\":"u8.ToArray(), _ => Encoding.UTF8.GetBytes(Query(2).GetRawText())
        };
        using var handler = new StubHttpHandler((_, _) => { var content = new ByteArrayContent(bytes); content.Headers.ContentType = new(kind == "media-type" ? "application/jsonp" : "application/json"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }); });
        await Assert.ThrowsAsync<InvalidDataException>(() => new MidjourneyVideoAdapter(new StubHttpClientFactory(handler), Options()).GetAsync("task", TestContext.Current.CancellationToken));
    }
    /// <summary>原始父任务、索引和媒体在首个等待前快照，调用者后续清空参数不修改已发送请求。</summary>
    [Fact]
    public async Task Snapshot_PrecedesTransportWait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var request = Request(2);
        using var handler = new StubHttpHandler(async (message, ct) =>
        { entered.SetResult(); await release.Task.WaitAsync(ct); using var document = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(ct)); Assert.True(JsonElement.DeepEquals(Case(2).GetProperty("wire_request"), document.RootElement)); return Json(Ack()); });
        var pending = new MidjourneyVideoAdapter(new StubHttpClientFactory(handler), Options(videoType: "vid_1.1_i2v_720")).SubmitAsync(request, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken); request.Parameters.Clear(); release.SetResult(); await pending;
    }
    /// <summary>编码、字节上限和预取消在明确边界执行，受限响应不能变成完整结果。</summary>
    [Fact]
    public async Task LimitsEncodingAndCancellation_AreEnforced()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(5)))); var factory = new StubHttpClientFactory(handler); var invalid = Request(); invalid.Parameters["state"] = "\ud800";
        await Assert.ThrowsAsync<EncoderFallbackException>(() => new MidjourneyVideoAdapter(factory, Options()).SubmitAsync(invalid, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => new MidjourneyVideoAdapter(factory, Options(requestLimit: 1)).SubmitAsync(Request(), TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MidjourneyVideoAdapter(factory, Options()).GetAsync("task", cancellation.Token)); Assert.Equal(0, handler.Calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => new MidjourneyVideoAdapter(factory, Options(responseLimit: 1)).GetAsync("mj-video-9007199254740993", TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>释放故障或最终取消不能被已经读取的成功结果掩盖，工厂仍为借用。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FinalDisposalAndCancellation_PrecedeDelivery(bool cancel)
    {
        using var cancellation = new CancellationTokenSource(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(5)))); using var factory = new MidjourneyTestHttpFactory(handler) { OnDispose = () => { if (cancel) cancellation.Cancel(); else throw new IOException("fixture disposal"); } };
        var adapter = new MidjourneyVideoAdapter(factory, Options()); if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetAsync("mj-video-9007199254740993", cancellation.Token)); else await Assert.ThrowsAsync<IOException>(() => adapter.GetAsync("mj-video-9007199254740993", cancellation.Token)); Assert.False(factory.Disposed); Assert.Equal(1, factory.ClientsDisposed);
    }
    /// <summary>配置摘要不泄露密钥，正常模块关闭也不处置借用工厂。</summary>
    [Fact]
    public async Task MetadataAndBorrowedLifetime_ArePreserved()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Ack()))); using var factory = new MidjourneyTestHttpFactory(handler); var options = Options(); var adapter = new MidjourneyVideoAdapter(factory, options);
        Assert.DoesNotContain(options.ApiSecret!, JsonSerializer.Serialize(options)); Assert.DoesNotContain(options.ApiSecret!, options.ToString()!); Assert.DoesNotContain(options.ApiSecret!, adapter.BindingIdentity);
        var loki = Build(factory); await loki.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken); await loki.DisposeAsync(); Assert.False(factory.Disposed); Assert.Equal(1, factory.ClientsDisposed);
    }
    /// <summary>跳转段和控制字符不得作为原生任务路径发送。</summary>
    [Theory] [InlineData(".")] [InlineData("..")] [InlineData("a\r\nb")]
    public async Task InvalidTaskId_FailsBeforeIo(string id)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); await Assert.ThrowsAsync<ArgumentException>(() => new MidjourneyVideoAdapter(new StubHttpClientFactory(handler), Options()).GetAsync(id, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>没有默认代理服务，错误地址、空密钥、缺失类型和非法策略在构造时拒绝。</summary>
    [Theory] [InlineData("root")] [InlineData("query")] [InlineData("secret")] [InlineData("empty-secret")]
    [InlineData("type")] [InlineData("request-limit")] [InlineData("response-limit")]
    public void InvalidConfiguration_IsRejected(string kind)
    {
        var options = kind switch
        {
            "root" => Options(root: "ftp://mj.invalid/mj/"), "query" => Options(root: "https://mj.invalid/mj/?mode=other"), "secret" => Options(secret: "bad\r\nsecret"),
            "empty-secret" => Options(secret: ""), "type" => Options(videoType: ""), "request-limit" => Options(requestLimit: 0), _ => Options(responseLimit: 0)
        };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); Assert.ThrowsAny<ArgumentException>(() => new MidjourneyVideoAdapter(new StubHttpClientFactory(handler), options)); Assert.Equal(0, handler.Calls);
    }
}
