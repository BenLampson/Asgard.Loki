using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.XAI.Tests.XaiFixture;

namespace Asgard.Loki.Video.XAI.Tests;

/// <summary>原生生成、编辑、延长、无回显查询和独立终态语义的公开 SDK 验证。</summary>
public sealed class XaiVideoTests
{
    /// <summary>原始媒体、参考声音、帧顺序、缺省参数和未知成员完整传递，任务引用经重建仍绑定原目标。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public async Task FullNativeWireAndResult_SurvivePublicSdk(int index)
    {
        var fixture = Case(index); var wire = fixture.GetProperty("wire_request"); var calls = 0;
        using var handler = new StubHttpHandler(async (message, ct) =>
        {
            Assert.Equal("Bearer fixture-xai-key", message.Headers.Authorization!.ToString()); Assert.Equal("application/json", Assert.Single(message.Headers.Accept).MediaType);
            if (++calls == 1)
            {
                Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://xai.invalid/" + fixture.GetProperty("path").GetString(), message.RequestUri!.AbsoluteUri);
                Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType); using var document = JsonDocument.Parse(await message.Content.ReadAsByteArrayAsync(ct));
                Assert.True(JsonElement.DeepEquals(wire, document.RootElement)); return Json(fixture.GetProperty("wire_response"));
            }
            Assert.Equal(HttpMethod.Get, message.Method); Assert.Null(message.Content); Assert.Equal("https://xai.invalid/v1/videos/xai-fixture-01", message.RequestUri!.AbsoluteUri); return QueryResponse(3);
        });
        var options = Options(Enum.Parse<XaiVideoOperation>(fixture.GetProperty("operation").GetString()!), wire.GetProperty("model").GetString()!); var factory = new StubHttpClientFactory(handler);
        await using var original = Build(factory, options); var submitted = await original.Video.SubmitAsync(Request(index), TestContext.Current.CancellationToken);
        Assert.Equal(VideoTaskState.Submitted, submitted.Snapshot.State); Assert.Null(submitted.Snapshot.NativeStatus); Assert.True(JsonElement.DeepEquals(fixture.GetProperty("wire_response"), submitted.Snapshot.Data));
        await using var rebuilt = Build(factory, options); var result = await rebuilt.Video.GetAsync(submitted.Reference, TestContext.Current.CancellationToken);
        Assert.Equal(submitted.Reference, result.Reference); Assert.Equal("xai-fixture-01", result.Snapshot.TaskId); Assert.Equal(VideoTaskState.Succeeded, result.Snapshot.State);
        Assert.True(JsonElement.DeepEquals(Query(3).GetProperty("body"), result.Snapshot.Data)); Assert.False(result.Snapshot.Data.TryGetProperty("request_id", out _));
        Assert.Equal(9007199254740993L, result.Snapshot.Data.GetProperty("usage").GetProperty("cost_in_usd_ticks").GetInt64()); Assert.Equal(2, handler.Calls);
        Assert.Equal(Query(3).GetProperty("body").GetRawText(), Encoding.UTF8.GetString(result.Snapshot.HttpResponse!.Body.Span));
    }
    /// <summary>原生状态、审核拦截、失败用量和真正空正文分别处理，不从进度或费用推导成功。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public async Task NativeLifecycle_IsIndependentOfUsageAndHttpSuccess(int index)
    {
        var fixture = Query(index); using var handler = new StubHttpHandler((_, _) => Task.FromResult(QueryResponse(index))); var adapter = new XaiVideoAdapter(new StubHttpClientFactory(handler), Options());
        var result = await adapter.GetAsync("xai-fixture-01", TestContext.Current.CancellationToken); var expected = Enum.Parse<VideoTaskState>(fixture.GetProperty("expected_state").GetString()!);
        Assert.Equal(expected, result.State); Assert.Equal(expected is VideoTaskState.Succeeded or VideoTaskState.Failed or VideoTaskState.Expired, result.IsTerminal);
        Assert.Equal((HttpStatusCode)fixture.GetProperty("http_status").GetInt32(), result.HttpResponse!.StatusCode); Assert.Equal("xai-fixture-01", result.TaskId); Assert.Equal(1, handler.Calls);
        if (fixture.TryGetProperty("body", out var body))
        {
            Assert.True(result.HasJsonData); Assert.True(JsonElement.DeepEquals(body, result.Data)); Assert.Equal(body.GetProperty("status").GetString(), result.NativeStatus); Assert.False(result.Data.TryGetProperty("request_id", out _));
            Assert.Equal(body.GetRawText(), Encoding.UTF8.GetString(result.HttpResponse.Body.Span)); Assert.Contains("application/json", result.HttpResponse.ContentType!);
        }
        else { Assert.False(result.HasJsonData); Assert.Equal(JsonValueKind.Undefined, result.Data.ValueKind); Assert.Null(result.NativeStatus); Assert.Empty(result.HttpResponse.Body.ToArray()); }
    }
    /// <summary>空正文查询也经公开入口保持原目标引用，不能由能力层补造 JSON 或改变任务身份。</summary>
    [Fact]
    public async Task EmptyAccepted_PreservesReferenceThroughPublicSdk()
    {
        var calls = 0; using var handler = new StubHttpHandler((_, _) => Task.FromResult(++calls == 1 ? Json(Case(0).GetProperty("wire_response")) : QueryResponse(0))); var factory = new StubHttpClientFactory(handler);
        await using var original = Build(factory); var submitted = await original.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        await using var restored = Build(factory); var queried = await restored.Video.GetAsync(submitted.Reference, TestContext.Current.CancellationToken);
        Assert.Equal(submitted.Reference, queried.Reference); Assert.Equal(submitted.Reference.TaskId, queried.Snapshot.TaskId); Assert.False(queried.Snapshot.HasJsonData); Assert.False(queried.Snapshot.IsTerminal);
        Assert.Equal(VideoTaskState.Running, queried.Snapshot.State); Assert.Equal(HttpStatusCode.Accepted, queried.Snapshot.HttpResponse!.StatusCode); Assert.Empty(queried.Snapshot.HttpResponse.Body.ToArray()); Assert.Equal(2, handler.Calls);
    }
    /// <summary>明确回显的原始请求 ID 必须匹配，不能因本地上下文覆盖而隐藏错误关联。</summary>
    [Fact]
    public async Task Query_ValidatesOptionalEchoAndEscapesOriginalId()
    {
        const string id = "opaque/a?b#c %素材"; var body = JsonNode.Parse(Query(2).GetProperty("body").GetRawText())!.AsObject(); body["request_id"] = id;
        using var handler = new StubHttpHandler((message, _) =>
        {
            Assert.Equal("https://xai.invalid:8443/prefix/v1/videos/" + Uri.EscapeDataString(id), message.RequestUri!.AbsoluteUri); Assert.Equal("Bearer sk-fictional-native", message.Headers.Authorization!.ToString()); return Task.FromResult(Json(body.ToJsonString()));
        });
        var result = await new XaiVideoAdapter(new StubHttpClientFactory(handler), Options(root: "https://xai.invalid:8443/prefix/", key: "sk-fictional-native")).GetAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(id, result.TaskId); Assert.Equal(id, result.Data.GetProperty("request_id").GetString()); Assert.Equal(1, handler.Calls);
    }
    /// <summary>提交返回原生即时终态时保持完整窗口，包括带错误的失败任务。</summary>
    [Theory] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public async Task Submission_RespectsImmediateNativeTerminal(int index)
    {
        var body = JsonNode.Parse(Query(index).GetProperty("body").GetRawText())!.AsObject(); body["request_id"] = "xai-fixture-01";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body.ToJsonString()))); var result = await new XaiVideoAdapter(new StubHttpClientFactory(handler), Options()).SubmitAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(Enum.Parse<VideoTaskState>(Query(index).GetProperty("expected_state").GetString()!), result.State); Assert.True(result.IsTerminal); Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(result.Data.GetRawText())));
    }
    /// <summary>原生审核标记未返回时保留缺省，不补造 true；明确 done 和有效 URL 才可完成。</summary>
    [Fact]
    public async Task MissingModerationFlag_IsNotInvented()
    {
        var body = JsonNode.Parse(Query(3).GetProperty("body").GetRawText())!.AsObject(); body["video"]!.AsObject().Remove("respect_moderation");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body.ToJsonString()))); var result = await new XaiVideoAdapter(new StubHttpClientFactory(handler), Options()).GetAsync("task", TestContext.Current.CancellationToken);
        Assert.Equal(VideoTaskState.Succeeded, result.State); Assert.False(result.Data.GetProperty("video").TryGetProperty("respect_moderation", out _));
    }
    /// <summary>媒体字段不切换明确的生成/编辑/延长接口，也不增加生成默认值或更改模型。</summary>
    [Theory] [InlineData(XaiVideoOperation.Generate, "generations")] [InlineData(XaiVideoOperation.Edit, "edits")] [InlineData(XaiVideoOperation.Extend, "extensions")]
    public async Task ExplicitOperation_DoesNotGuessOrInsertDefaults(XaiVideoOperation operation, string path)
    {
        var parameters = new JsonObject { ["video"] = new JsonObject { ["url"] = "https://media.example/video.mp4" }, ["generate_audio"] = false, ["Future"] = null };
        using var handler = new StubHttpHandler(async (message, ct) =>
        {
            Assert.Equal("https://xai.invalid/v1/videos/" + path, message.RequestUri!.AbsoluteUri); var expected = parameters.DeepClone().AsObject(); expected["model"] = "opaque-model";
            using var document = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(ct)); Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(document.RootElement.GetRawText()))); return Json(Case(0).GetProperty("wire_response"));
        });
        await new XaiVideoAdapter(new StubHttpClientFactory(handler), Options(operation, "opaque-model")).SubmitAsync(new() { Model = "video", Parameters = parameters }, TestContext.Current.CancellationToken);
    }
    /// <summary>共享查询路径不会削弱完整目标绑定，任一关键配置漂移都在查询 IO 前拒绝。</summary>
    [Theory] [InlineData("root")] [InlineData("model")] [InlineData("operation")] [InlineData("key")]
    [InlineData("client")] [InlineData("request-limit")] [InlineData("response-limit")]
    public async Task TargetDrift_IsRejectedBeforeQueryIo(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Case(0).GetProperty("wire_response")))); var factory = new StubHttpClientFactory(handler);
        await using var original = Build(factory); var task = await original.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        var options = kind switch
        {
            "root" => Options(root: "https://other.invalid/"), "model" => Options(model: "another-model"), "operation" => Options(XaiVideoOperation.Edit), "key" => Options(key: "another-key"),
            "client" => Options(client: "another-client"), "request-limit" => Options(requestLimit: 1024), _ => Options(responseLimit: 1024)
        };
        await using var changed = Build(factory, options); await Assert.ThrowsAsync<InvalidOperationException>(() => changed.Video.GetAsync(task.Reference, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>同步 API/HTTP 错误保留原始正文；HTTP 200 错误也不伪装成一个新任务。</summary>
    [Theory] [InlineData(200, false)] [InlineData(400, false)] [InlineData(401, false)] [InlineData(429, false)] [InlineData(503, false)]
    [InlineData(200, true)] [InlineData(400, true)] [InlineData(401, true)] [InlineData(429, true)] [InlineData(503, true)]
    public async Task NativeApiError_PreservesRawWindowWithoutRetry(int status, bool query)
    {
        var body = Data().GetProperty("api_error"); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body, (HttpStatusCode)status))); var adapter = new XaiVideoAdapter(new StubHttpClientFactory(handler), Options());
        var error = await Assert.ThrowsAsync<VideoUpstreamException>(() => query ? adapter.GetAsync("task", TestContext.Current.CancellationToken) : adapter.SubmitAsync(Request(), TestContext.Current.CancellationToken)); Assert.Equal(status, (int)error.StatusCode); Assert.Equal(body.GetRawText(), Encoding.UTF8.GetString(error.ResponseBody.Span)); Assert.Equal(1, handler.Calls);
    }
}
