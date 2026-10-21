using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.CompactionFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>验证压缩操作的真实 HTTP 报文、完整结果、失败与取消，不调用真实模型。</summary>
public sealed class ResponsesCompactionTests
{
    /// <summary>未知字段中的零、false、null、容器及 Unicode 与完整输入均原样发送。</summary>
    [Theory]
    [InlineData("0")] [InlineData("false")] [InlineData("null")] [InlineData("[]")]
    [InlineData("{}")] [InlineData("\"\"")] [InlineData("{\"中文\":[0,false,null]}")]
    public async Task NativeWire_PreservesInputAndExtensions(string value)
    {
        var input = JsonNode.Parse("""[{"type":"message","role":"user","content":[{"type":"input_text","text":"完整窗口"}]},{"type":"compaction","id":"opaque-input","encrypted_content":"preserve"}]""")!;
        var before = input.ToJsonString();
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://a.invalid/custom/v1/responses/compact", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal("test-key-a", request.Headers.Authorization.Parameter);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            Assert.Equal("actual-a", body["model"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(input, body["input"]));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(value), body["future"]));
            Assert.Equal("Explicit instructions", body["instructions"]!.GetValue<string>());
            Assert.False(body.ContainsKey("stream")); Assert.False(body.ContainsKey("store")); Assert.False(body.ContainsKey("background"));
            return Json();
        });
        await using var client = Build(handler);
        var result = await client.LLM.Responses.CompactAsync(new()
        {
            Model = "public", Input = input, Parameters = new()
            {
                ["future"] = JsonSerializer.Deserialize<JsonElement>(value),
                ["instructions"] = JsonSerializer.SerializeToElement("Explicit instructions")
            }
        }, Call(), TestContext.Current.CancellationToken);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Window), JsonNode.Parse(result.Data.GetRawText())));
        Assert.Equal(4, result.Output.GetArrayLength()); Assert.Equal("cmp_1", result.Id);
        Assert.Equal("opaque-signed-state", result.Output[3].GetProperty("encrypted_content").GetString());
        Assert.Equal("public", result.Metadata.PublicModel); Assert.Equal("actual-a", result.Metadata.UpstreamModel); Assert.Null(result.Metadata.Conversion);
        Assert.Equal(before, input.ToJsonString()); Assert.Equal(1, handler.Calls);
    }

    /// <summary>原生文本输入和省略输入都不会被改成另一种历史结构。</summary>
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task OptionalInput_PreservesNativeShape(bool provide)
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            Assert.Equal(provide, body.ContainsKey("input"));
            if (provide) Assert.Equal("plain input", body["input"]!.GetValue<string>());
            return Json();
        });
        await using var client = Build(handler);
        await client.LLM.Responses.CompactAsync(new() { Model = "public", Input = provide ? JsonValue.Create("plain input") : null }, Call(), TestContext.Current.CancellationToken);
    }

    /// <summary>错误状态和正文完整传播，压缩生成请求不会重发。</summary>
    [Theory]
    [InlineData(400)] [InlineData(429)] [InlineData(503)]
    public async Task HttpFailure_IsNotRetried(int code)
    {
        const string error = "{\"error\":{\"code\":\"upstream\",\"future\":false}}";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(error, (HttpStatusCode)code)));
        await using var client = Build(handler);
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => client.LLM.Responses.CompactAsync(
            new() { Model = "public", Input = JsonValue.Create("hi") }, Call(), TestContext.Current.CancellationToken));
        Assert.Equal((HttpStatusCode)code, failure.StatusCode); Assert.Equal(error, failure.ResponseBody); Assert.Equal(1, handler.Calls);
    }

    /// <summary>普通响应、缺失窗口及带错误的 JSON 不能被包装为成功压缩结果。</summary>
    [Theory]
    [InlineData("{\"id\":\"r1\",\"object\":\"response\",\"output\":[]}", false)]
    [InlineData("{\"object\":\"response.compaction\"}", false)]
    [InlineData("{\"object\":\"response.compaction\",\"output\":{}}", false)]
    [InlineData("{\"object\":\"response.compaction\",\"output\":[],\"error\":{\"message\":\"failed\"}}", true)]
    public async Task InvalidResult_IsRejected(string body, bool nativeError)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body)));
        await using var client = Build(handler);
        if (nativeError)
        {
            var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => client.LLM.Responses.CompactAsync(new() { Model = "public" }, Call(), TestContext.Current.CancellationToken));
            Assert.Equal(HttpStatusCode.OK, error.StatusCode); Assert.Equal(body, error.ResponseBody);
        }
        else await Assert.ThrowsAsync<InvalidDataException>(() => client.LLM.Responses.CompactAsync(new() { Model = "public" }, Call(), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>取消前置调用不应创建 Session 或发出 HTTP。</summary>
    [Fact]
    public async Task CancelledCall_DoesNotBindOrSend()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json())); var store = Store();
        await using var client = Build(handler, store); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.LLM.Responses.CompactAsync(new() { Model = "public" }, Call(), cancellation.Token));
        Assert.Equal(0, handler.Calls); Assert.Null(await store.GetAsync("session", TestContext.Current.CancellationToken));
    }

    /// <summary>在途取消传播到底层请求，不恢复连接或补发生成。</summary>
    [Fact]
    public async Task InFlightCancellation_ReachesHttp()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHttpHandler(async (_, token) =>
        {
            entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return Json();
        });
        await using var client = Build(handler); using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = client.LLM.Responses.CompactAsync(new() { Model = "public" }, Call(), cancellation.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Equal(1, handler.Calls);
    }

    /// <summary>扩展字段不能覆盖绑定或传输控制字段，错误在 Session 创建前报告。</summary>
    [Theory]
    [InlineData("model")] [InlineData("input")] [InlineData("previous_response_id")] [InlineData("stream")]
    public async Task ReservedParameter_DoesNotBindOrSend(string name)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json())); var store = Store();
        await using var client = Build(handler, store);
        await Assert.ThrowsAsync<ArgumentException>(() => client.LLM.Responses.CompactAsync(new()
        { Model = "public", Parameters = new() { [name] = JsonSerializer.SerializeToElement(false) } }, Call(), TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls); Assert.Null(await store.GetAsync("session", TestContext.Current.CancellationToken));
    }

    /// <summary>非法输入类型明确拒绝，不把 JSON 对象或数值当成文本。</summary>
    [Theory]
    [InlineData("0")] [InlineData("false")] [InlineData("{}")]
    public async Task InvalidInput_DoesNotSend(string input)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json())); await using var client = Build(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => client.LLM.Responses.CompactAsync(new()
        { Model = "public", Input = JsonNode.Parse(input) }, Call(), TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>缓存故障不触发无状态压缩、目标切换或重复请求。</summary>
    [Fact]
    public async Task CacheFailure_DoesNotSend()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json()));
        var store = new DistributedCacheLlmSessionStore(new Asgard.Loki.LLM.Tests.FailingDistributedCache(), new Asgard.Loki.LLM.Tests.TestSessionLock());
        await using var client = Build(handler, store);
        await Assert.ThrowsAsync<IOException>(() => client.LLM.Responses.CompactAsync(new() { Model = "public" }, Call(), TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
    }
}
