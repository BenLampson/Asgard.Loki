using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.GoogleVertex.Tests.VertexVideoFixture;

namespace Asgard.Loki.Video.GoogleVertex.Tests;

/// <summary>验证 Vertex Interactions 的真实提交、单次查询及任务隔离契约。</summary>
public sealed class VertexOmniVideoTests
{
    private const string Endpoint = "https://vertex.invalid/v1beta1/projects/fixture-project/locations/global/interactions";
    private const string Model = "gemini-omni-1.2-flash";
    private const string Accepted = """{"id":"interaction-123","status":"in_progress","object":"interaction"}""";
    private const string Complete = """
        {"id":"interaction-123","status":"completed","object":"interaction","model":"gemini-omni-1.2-flash",
         "steps":[{"type":"thought","signature":"opaque"},{"type":"model_output","content":[
           {"type":"video","data":"AAE=","mime_type":"video/mp4"},{"type":"video","uri":"gs://example/output.mp4","mime_type":"video/mp4"}]}],
         "usage":{"total_tokens":9007199254740993},"extension":{"unknown":false}}
        """;

    /// <summary>原生请求含多张参考图；持久化后查询只发 GET，不把 URI 结果下载到本地。</summary>
    [Fact]
    public async Task SubmitAndQuery_PreserveAllReferencesAndResults()
    {
        var clock = new VertexVideoClock();
        using var source = new VertexVideoTokenSource(clock);
        var calls = 0;
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("Bearer fixture-token-" + ++calls, request.Headers.Authorization!.ToString());
            if (calls == 1)
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(Endpoint, request.RequestUri!.AbsoluteUri);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token));
                using var expected = JsonDocument.Parse("""
                    {"model":"gemini-omni-1.2-flash","background":true,"input":[{"type":"text","text":"Use both images."},
                    {"type":"image","uri":"gs://example/first.png","mime_type":"image/png"},
                    {"type":"image","uri":"gs://example/second.png","mime_type":"image/png"}],
                    "response_format":[{"type":"video","duration":"15s","delivery":"inline"}],
                    "generation_config":{"seed":42}}
                    """);
                Assert.True(JsonElement.DeepEquals(expected.RootElement, body.RootElement));
                return Json(Accepted);
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(Endpoint + "/interaction-123", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Content);
            return Json(Complete);
        });
        var factory = new StubHttpClientFactory(handler);
        var ct = TestContext.Current.CancellationToken;
        await using var sdk = BuildOmni(factory, source, clock);
        var submitted = await sdk.Video.SubmitAsync(OmniRequest(), ct);
        Assert.Equal(VideoTaskState.Running, submitted.Snapshot.State);
        var persisted = JsonSerializer.Deserialize<VideoTaskReference>(JsonSerializer.Serialize(submitted.Reference))!;
        await using var rebuilt = BuildOmni(factory, source, clock);
        var result = await rebuilt.Video.GetAsync(persisted, ct);
        Assert.Equal(VideoTaskState.Succeeded, result.Snapshot.State);
        using var expected = JsonDocument.Parse(Complete);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, result.Snapshot.Data));
        await using var other = BuildOmni(factory, source, clock, Endpoint.Replace("/global/", "/us/", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.Video.GetAsync(persisted, ct));
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, source.Calls);
    }

    /// <summary>失败原因保留；完成必须有视频；响应 ID 不可串到其他任务。</summary>
    [Theory]
    [InlineData("""{"id":"interaction-123","status":"failed","errors":[{"code":"native_failure","message":"native failure"}]}""", VideoTaskState.Failed)]
    [InlineData("""{"id":"interaction-123","status":"incomplete","error":{"message":"native limit"}}""", VideoTaskState.Failed)]
    [InlineData("""{"id":"interaction-123","status":"requires_action"}""", VideoTaskState.Unknown)]
    [InlineData("""{"id":"interaction-123","status":"completed","steps":[{"type":"thought"}]}""", null)]
    [InlineData("""{"id":"other","status":"in_progress"}""", null)]
    public async Task Query_DistinguishesInvalidAndNativeStates(string body, VideoTaskState? state)
    {
        var clock = new VertexVideoClock();
        using var source = new VertexVideoTokenSource(clock);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body)));
        var adapter = new GoogleVertexOmniVideoAdapter(new StubHttpClientFactory(handler), source, Options(), clock);
        var ct = TestContext.Current.CancellationToken;
        if (state is null)
            await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetAsync("interaction-123", ct));
        else
        {
            var result = await adapter.GetAsync("interaction-123", ct);
            Assert.Equal(state, result.State);
            Assert.Equal(body, result.Data.GetRawText());
        }
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>不可查询或流式配置，以及路径跳转，在取令牌前拒绝。</summary>
    [Fact]
    public async Task UnsupportedInput_FailsBeforeTokenOrHttp()
    {
        var clock = new VertexVideoClock();
        using var source = new VertexVideoTokenSource(clock);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Accepted)));
        var adapter = new GoogleVertexOmniVideoAdapter(new StubHttpClientFactory(handler), source, Options(), clock);
        var ct = TestContext.Current.CancellationToken;
        var request = OmniRequest();
        request.Parameters["stream"] = true;
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.SubmitAsync(request, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.GetAsync("../other", ct));
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>准备固定的测试模型和原生区域地址。</summary>
    private static GoogleVertexOmniVideoOptions Options(string endpoint = Endpoint) =>
        new() { InteractionsEndpoint = new(endpoint), Model = Model, PrincipalIdentity = "fixture-principal" };

    /// <summary>通过公共 Video 路由提交并用原任务引用查询。</summary>
    private static Asgard.Loki.Abstractions.ILokiClient BuildOmni(IHttpClientFactory factory, IGoogleVertexVideoTokenSource source,
        TimeProvider clock, string endpoint = Endpoint) => new LokiClientBuilder().AddVideo(new VideoOptions
    {
        Routes = [new() { Model = "omni", Targets = [new GoogleVertexOmniVideoAdapter(factory, source, Options(endpoint), clock)] }]
    }).Build();

    /// <summary>独立构造多图 Interactions 原生提交样本。</summary>
    private static VideoTaskRequest OmniRequest() => new()
    {
        Model = "omni",
        Parameters = JsonNode.Parse("""
            {"background":true,"input":[{"type":"text","text":"Use both images."},
            {"type":"image","uri":"gs://example/first.png","mime_type":"image/png"},
            {"type":"image","uri":"gs://example/second.png","mime_type":"image/png"}],
            "response_format":[{"type":"video","duration":"15s","delivery":"inline"}],"generation_config":{"seed":42}}
            """)!.AsObject()
    };
}
