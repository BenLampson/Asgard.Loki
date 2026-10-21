using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.Volcengine.Tests.JimengFixture;

namespace Asgard.Loki.Video.Volcengine.Tests;

/// <summary>验证即梦 Visual 原生协议、独立签名、无回传 ID 的关联和完整目标持久化。</summary>
public sealed class JimengVideoTests
{
    /// <summary>文本、图片与首尾帧保持原生 req_key/正文，不按媒体内容重写服务标识。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task SubmitAndQuery_PreserveCompleteNativeWire(int index)
    {
        var wire = Data().GetProperty("submissions")[index].GetProperty("wire_request"); var requestKey = wire.GetProperty("req_key").GetString()!; var calls = 0;
        using var handler = new StubHttpHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), request.Headers.GetValues("X-Content-Sha256").Single());
            Assert.Equal("jimeng.invalid", request.Headers.Host); Assert.Equal("application/json", request.Content.Headers.ContentType!.ToString());
            Assert.StartsWith("HMAC-SHA256 Credential=fixture-access/20261020/cn-north-1/cv/request, ", request.Headers.GetValues("Authorization").Single());
            using var document = JsonDocument.Parse(bytes);
            if (++calls == 1)
            {
                Assert.Equal("https://jimeng.invalid/?Action=CVSync2AsyncSubmitTask&Version=2022-08-31", request.RequestUri!.AbsoluteUri);
                Assert.True(JsonElement.DeepEquals(wire, document.RootElement)); return ArkFixture.Json(Data().GetProperty("submit_response"));
            }
            Assert.Equal("https://jimeng.invalid/?Action=CVSync2AsyncGetResult&Version=2022-08-31", request.RequestUri!.AbsoluteUri);
            Assert.Equal(requestKey, document.RootElement.GetProperty("req_key").GetString()); Assert.Equal("jimeng-fixture-01", document.RootElement.GetProperty("task_id").GetString());
            Assert.Equal(2, document.RootElement.EnumerateObject().Count()); return ArkFixture.Json(Query(2));
        });
        var factory = new StubHttpClientFactory(handler); var options = Options(requestKey); await using var original = Build(factory, options);
        var submitted = await original.Video.SubmitAsync(Request(index), TestContext.Current.CancellationToken); Assert.Equal(VideoTaskState.Submitted, submitted.Snapshot.State); Assert.Null(submitted.Snapshot.NativeStatus);
        Assert.True(JsonElement.DeepEquals(Data().GetProperty("submit_response"), submitted.Snapshot.Data));
        var persisted = JsonSerializer.Deserialize<VideoTaskReference>(JsonSerializer.Serialize(submitted.Reference))!; await using var rebuilt = Build(factory, options);
        var current = await rebuilt.Video.GetAsync(persisted, TestContext.Current.CancellationToken);
        Assert.Equal(VideoTaskState.Succeeded, current.Snapshot.State); Assert.Equal("jimeng-fixture-01", current.Snapshot.TaskId); Assert.True(JsonElement.DeepEquals(Query(2), current.Snapshot.Data));
        Assert.False(current.Snapshot.Data.GetProperty("data").TryGetProperty("task_id", out _)); Assert.False(current.Snapshot.Data.TryGetProperty("req_key", out _)); Assert.Equal(2, handler.Calls);
    }
    /// <summary>完整签名必须匹配独立向量，包括 cv 服务、区域、非默认端口和临时凭据。</summary>
    [Theory] [InlineData(0)] [InlineData(1)]
    public async Task Signature_MatchesIndependentVisualVector(int index)
    {
        var vector = Data().GetProperty("signatures")[index]; var submit = index == 0;
        using var handler = new StubHttpHandler(async (request, ct) =>
        {
            Assert.Equal(vector.GetProperty("body").GetString(), await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(vector.GetProperty("authorization").GetString(), request.Headers.GetValues("Authorization").Single());
            Assert.Equal(vector.GetProperty("date").GetString(), request.Headers.GetValues("X-Date").Single()); Assert.Equal(vector.GetProperty("payload_hash").GetString(), request.Headers.GetValues("X-Content-Sha256").Single());
            Assert.Equal(vector.GetProperty("host").GetString(), request.Headers.Host);
            if (index == 1) Assert.Equal("fixture-session-token", request.Headers.GetValues("X-Security-Token").Single()); else Assert.False(request.Headers.Contains("X-Security-Token"));
            return ArkFixture.Json(submit ? Data().GetProperty("submit_response") : Query(2));
        });
        var options = Options(vector.GetProperty("request_key").GetString()!, vector.GetProperty("host").GetString()!, token: vector.GetProperty("session_token").GetString());
        var adapter = new JimengVideoAdapter(new StubHttpClientFactory(handler), options, new AssetTestClock());
        if (submit) await adapter.SubmitAsync(new() { Model = "video", Parameters = JsonNode.Parse(vector.GetProperty("parameters").GetRawText())!.AsObject() }, TestContext.Current.CancellationToken);
        else await adapter.GetAsync("jimeng-fixture-01", TestContext.Current.CancellationToken);
    }
    /// <summary>not_found 不代表已知过期；expired 才是明确终态，未来状态不构造失败或成功。</summary>
    [Theory] [InlineData(0, VideoTaskState.Queued)] [InlineData(1, VideoTaskState.Running)] [InlineData(2, VideoTaskState.Succeeded)]
    [InlineData(3, VideoTaskState.Unknown)] [InlineData(4, VideoTaskState.Expired)] [InlineData(5, VideoTaskState.Unknown)]
    public async Task NativeLifecycle_PreservesOriginalData(int index, VideoTaskState expected)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ArkFixture.Json(Query(index)))); var adapter = new JimengVideoAdapter(new StubHttpClientFactory(handler), Options(), new AssetTestClock());
        var result = await adapter.GetAsync("jimeng-fixture-01", TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State); Assert.Equal(expected is VideoTaskState.Succeeded or VideoTaskState.Expired, result.IsTerminal); Assert.Equal("jimeng-fixture-01", result.TaskId);
        Assert.Equal(Query(index).GetProperty("data").GetProperty("status").GetString(), result.NativeStatus); Assert.True(JsonElement.DeepEquals(Query(index), result.Data)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生 req_json、水印配置、未知查询字段和非数字任务 ID 不被解析、改写或编码成 URL。</summary>
    [Fact]
    public async Task QueryParameters_AreOriginalAndSnapshottedBeforeWait()
    {
        const string id = "opaque/a?b#c %素材"; var input = JsonNode.Parse(Data().GetProperty("query_parameters").GetRawText())!.AsObject(); var expected = input.DeepClone();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHttpHandler(async (request, ct) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(ct); var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            Assert.Equal("fixture_req_key", body["req_key"]!.GetValue<string>()); Assert.Equal(id, body["task_id"]!.GetValue<string>());
            body.Remove("req_key"); body.Remove("task_id"); Assert.True(JsonNode.DeepEquals(expected, body)); return ArkFixture.Json(Query(2));
        });
        var adapter = new JimengVideoAdapter(new StubHttpClientFactory(handler), Options(), new AssetTestClock()); var pending = adapter.GetAsync(id, input, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken); input.Clear(); release.SetResult(); var result = await pending; Assert.Equal(id, result.TaskId); Assert.Equal(1, handler.Calls);
    }
    /// <summary>HTTP、原生处理错误和 OpenAPI 认证错误全部保留原始字节，不能推断任务永久失败或自动重试。</summary>
    [Theory] [InlineData("native_error", 200)] [InlineData("native_error", 429)] [InlineData("openapi_error", 200)] [InlineData("openapi_error", 403)]
    public async Task NativeErrors_PreserveFullEnvelope(string name, int status)
    {
        var native = Data().GetProperty(name); using var handler = new StubHttpHandler((_, _) => Task.FromResult(ArkFixture.Json(native, (HttpStatusCode)status)));
        var adapter = new JimengVideoAdapter(new StubHttpClientFactory(handler), Options()); var error = await Assert.ThrowsAsync<VideoUpstreamException>(() => adapter.GetAsync("task", TestContext.Current.CancellationToken));
        Assert.Equal(status, (int)error.StatusCode); Assert.Equal(native.GetRawText(), Encoding.UTF8.GetString(error.ResponseBody.Span)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>后续引用不能迁移至其他 req_key、服务器、AK/SK、临时令牌或资源策略。</summary>
    [Theory] [InlineData("key")] [InlineData("root")] [InlineData("access")] [InlineData("secret")] [InlineData("token")]
    [InlineData("client")] [InlineData("request-limit")] [InlineData("response-limit")]
    public async Task CompleteTargetDrift_FailsBeforeQueryIo(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ArkFixture.Json(Data().GetProperty("submit_response")))); var factory = new StubHttpClientFactory(handler);
        await using var original = Build(factory); var submitted = await original.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        var options = change switch
        {
            "key" => Options(requestKey: "other_req_key"), "root" => Options(host: "other.invalid"), "access" => Options(access: "other-access"), "secret" => Options(secret: "other-secret"),
            "token" => Options(token: "other-token"), "client" => Options(client: "other-client"), "request-limit" => Options(requestLimit: 1000), _ => Options(responseLimit: 1000)
        };
        await using var changed = Build(factory, options); await Assert.ThrowsAsync<InvalidOperationException>(() => changed.Video.GetAsync(submitted.Reference, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
}
