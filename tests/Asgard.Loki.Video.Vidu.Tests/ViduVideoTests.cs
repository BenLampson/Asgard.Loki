using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.Vidu.Tests.ViduFixture;

namespace Asgard.Loki.Video.Vidu.Tests;

/// <summary>四种原生生成操作、不同提交/查询身份字段与全部结果的公开 SDK 验证。</summary>
public sealed class ViduVideoTests
{
    /// <summary>完整样本经公开入口保持原生字段、Token 认证、两个结果身份和持久化绑定。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task SubmitThenRebuildAndQuery_PreservesNativeWire(int index)
    {
        var fixture = Case(index); var wire = fixture.GetProperty("wire_request"); var calls = 0;
        using var handler = new StubHttpHandler(async (message, ct) =>
        {
            Assert.Equal("Token fixture-vidu-key", message.Headers.Authorization!.ToString()); Assert.Equal("application/json", Assert.Single(message.Headers.Accept).MediaType);
            if (++calls == 1)
            {
                Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://vidu.invalid/" + fixture.GetProperty("path").GetString(), message.RequestUri!.AbsoluteUri);
                Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
                using var document = JsonDocument.Parse(await message.Content.ReadAsByteArrayAsync(ct)); Assert.True(JsonElement.DeepEquals(wire, document.RootElement));
                return Json(fixture.GetProperty("wire_response"));
            }
            Assert.Equal(HttpMethod.Get, message.Method); Assert.Null(message.Content); Assert.Equal("https://vidu.invalid/ent/v2/tasks/vidu-fixture-01/creations", message.RequestUri!.AbsoluteUri);
            return Json(Query(5));
        });
        var options = Options(Enum.Parse<ViduVideoOperation>(fixture.GetProperty("operation").GetString()!), wire.GetProperty("model").GetString()!); var factory = new StubHttpClientFactory(handler);
        await using var original = Build(factory, options); var submitted = await original.Video.SubmitAsync(Request(index), TestContext.Current.CancellationToken);
        Assert.Equal(VideoTaskState.Submitted, submitted.Snapshot.State); Assert.True(JsonElement.DeepEquals(fixture.GetProperty("wire_response"), submitted.Snapshot.Data));
        await using var rebuilt = Build(factory, options); var current = await rebuilt.Video.GetAsync(submitted.Reference, TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(Query(5), current.Snapshot.Data)); Assert.Equal("9007199254740994", current.Snapshot.Data.GetProperty("creations")[1].GetProperty("id").GetString());
        Assert.Equal(VideoTaskState.Succeeded, current.Snapshot.State); Assert.True(current.Snapshot.IsTerminal); Assert.Equal(2, handler.Calls);
    }
    /// <summary>原生创建、排队、执行、失败、未来状态及成功有各自明确含义，不从 HTTP 200 推断成功。</summary>
    [Theory] [InlineData(0, VideoTaskState.Submitted)] [InlineData(1, VideoTaskState.Queued)] [InlineData(2, VideoTaskState.Running)]
    [InlineData(3, VideoTaskState.Failed)] [InlineData(4, VideoTaskState.Unknown)] [InlineData(5, VideoTaskState.Succeeded)]
    public async Task Query_MapsOnlyKnownNativeStates(int index, VideoTaskState state)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(index)))); var adapter = new ViduVideoAdapter(new StubHttpClientFactory(handler), Options());
        var result = await adapter.GetAsync("vidu-fixture-01", TestContext.Current.CancellationToken);
        Assert.Equal(state, result.State); Assert.Equal(state is VideoTaskState.Succeeded or VideoTaskState.Failed, result.IsTerminal); Assert.True(JsonElement.DeepEquals(Query(index), result.Data)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>提交可能已经失败或完成，完整原生窗口保留，不硬编码为接受。</summary>
    [Theory] [InlineData(3)] [InlineData(5)]
    public async Task Submission_RespectsImmediateNativeTerminal(int index)
    {
        var body = JsonNode.Parse(Query(index).GetRawText())!.AsObject(); body["task_id"] = body["id"]!.DeepClone(); body.Remove("id");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body.ToJsonString()))); var adapter = new ViduVideoAdapter(new StubHttpClientFactory(handler), Options());
        var result = await adapter.SubmitAsync(Request(), TestContext.Current.CancellationToken); Assert.True(result.IsTerminal); Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(result.Data.GetRawText())));
    }
    /// <summary>无状态提交确认只表示接受，不补造原生 state。</summary>
    [Fact]
    public async Task BareAcknowledgement_RemainsNonterminal()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json("{\"task_id\":\"task\",\"Future\":null}"))); var adapter = new ViduVideoAdapter(new StubHttpClientFactory(handler), Options());
        var result = await adapter.SubmitAsync(Request(), TestContext.Current.CancellationToken); Assert.Null(result.NativeStatus); Assert.Equal(VideoTaskState.Submitted, result.State); Assert.False(result.IsTerminal);
    }
    /// <summary>操作只能显式指定，图片数量、metadata.action 和模型文本不能改写路由或填充生成默认值。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task ExplicitOperation_DoesNotGuessOrAddDefaults(int index)
    {
        var fixture = Case(index); var parameters = new JsonObject { ["images"] = new JsonArray("one-image"), ["metadata"] = new JsonObject { ["action"] = "other-action" } };
        using var handler = new StubHttpHandler(async (message, ct) =>
        {
            Assert.Equal("https://vidu.invalid/" + fixture.GetProperty("path").GetString(), message.RequestUri!.AbsoluteUri);
            var expected = parameters.DeepClone().AsObject(); expected["model"] = "opaque-model"; using var document = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(ct)); Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(document.RootElement.GetRawText())));
            return Json(fixture.GetProperty("wire_response"));
        });
        var adapter = new ViduVideoAdapter(new StubHttpClientFactory(handler), Options(Enum.Parse<ViduVideoOperation>(fixture.GetProperty("operation").GetString()!), "opaque-model"));
        await adapter.SubmitAsync(new() { Model = "video", Parameters = parameters }, TestContext.Current.CancellationToken);
    }
    /// <summary>原生 Token 不转换成 Bearer；调用前缀及任务 ID 作为单独路径段保持，空白不修剪。</summary>
    [Fact]
    public async Task Query_PreservesOpaquePathAndCredential()
    {
        const string id = "opaque/a?b#c %素材";
        using var handler = new StubHttpHandler((message, _) =>
        {
            Assert.Equal("Token sk-fictional-native", message.Headers.Authorization!.ToString()); Assert.Equal("https://vidu.invalid:8443/prefix/ent/v2/tasks/" + Uri.EscapeDataString(id) + "/creations", message.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json(new JsonObject { ["id"] = id, ["state"] = "processing" }.ToJsonString()));
        });
        var adapter = new ViduVideoAdapter(new StubHttpClientFactory(handler), Options(root: "https://vidu.invalid:8443/prefix/", key: "sk-fictional-native"));
        Assert.Equal(id, (await adapter.GetAsync(id, TestContext.Current.CancellationToken)).TaskId);
    }
    /// <summary>重建任务后任何完整目标变更都在查询 IO 前拒绝，即使四种操作共用查询路径。</summary>
    [Theory] [InlineData("root")] [InlineData("model")] [InlineData("operation")] [InlineData("key")]
    [InlineData("client")] [InlineData("request-limit")] [InlineData("response-limit")]
    public async Task TargetDrift_FailsBeforeIo(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Case(0).GetProperty("wire_response")))); var factory = new StubHttpClientFactory(handler);
        await using var original = Build(factory); var task = await original.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        var options = change switch
        {
            "root" => Options(root: "https://other.invalid/"), "model" => Options(model: "other-model"), "operation" => Options(ViduVideoOperation.ReferenceToVideo),
            "key" => Options(key: "other-key"), "client" => Options(client: "other-client"), "request-limit" => Options(requestLimit: 1024), _ => Options(responseLimit: 1024)
        };
        await using var changed = Build(factory, options); await Assert.ThrowsAsync<InvalidOperationException>(() => changed.Video.GetAsync(task.Reference, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>HTTP 错误和 HTTP 200 原生错误保留原始正文与媒体类型，不重试。</summary>
    [Theory] [InlineData(200)] [InlineData(400)] [InlineData(401)] [InlineData(429)] [InlineData(503)]
    public async Task NativeError_IsRawAndNeverRetried(int status)
    {
        var body = Data().GetProperty("api_error"); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body, (HttpStatusCode)status))); var adapter = new ViduVideoAdapter(new StubHttpClientFactory(handler), Options());
        var error = await Assert.ThrowsAsync<VideoUpstreamException>(() => adapter.GetAsync("task", TestContext.Current.CancellationToken));
        Assert.Equal(status, (int)error.StatusCode); Assert.Equal(body.GetRawText(), Encoding.UTF8.GetString(error.ResponseBody.Span)); Assert.Contains("application/json", error.ContentType!); Assert.Equal(1, handler.Calls);
    }
}
