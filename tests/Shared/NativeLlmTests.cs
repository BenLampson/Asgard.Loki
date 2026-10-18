using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.Tests;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asgard.Loki.Testing;

/// <summary>通过公共入口检查原生协议保真、路由、错误与流式完成。</summary>
public abstract class NativeLlmTests
{
    /// <summary>由具体供应商测试选择协议。</summary>
    protected abstract LlmProtocol Protocol { get; }
    /// <summary>创建实际供应商适配器。</summary>
    protected abstract ILlmProvider Provider(IHttpClientFactory factory);
    private bool Claude => Protocol == LlmProtocol.ClaudeMessages;

    /// <summary>覆盖字段存在性和特殊 JSON 值，不把发送成功视为模型效果。</summary>
    [Theory]
    [InlineData(false, "0")]
    [InlineData(true, "0")]
    [InlineData(false, "false")]
    [InlineData(true, "false")]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    [InlineData(false, "[]")]
    [InlineData(true, "[]")]
    [InlineData(false, "{}")]
    [InlineData(true, "{\"中文\":[0,false,null]}")]
    public async Task NativePipeline_PreservesWireValues(bool stream, string json)
    {
        JsonObject? sent = null;
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var expected = Claude ? "https://native.invalid/custom/messages" : "https://native.invalid/custom/models/model-a:" + (stream ? "streamGenerateContent?alt=sse" : "generateContent");
            Assert.Equal(expected, request.RequestUri!.AbsoluteUri);
            Assert.Equal("bound-key", Assert.Single(request.Headers.GetValues(Claude ? "x-api-key" : "x-goog-api-key")));
            Assert.Null(request.Headers.Authorization);
            if (Claude) Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
            sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            if (Claude) { Assert.Equal("model-a", sent["model"]!.GetValue<string>()); Assert.Equal(stream, sent["stream"]!.GetValue<bool>()); }
            else { Assert.False(sent.ContainsKey("model")); Assert.False(sent.ContainsKey("stream")); }
            return stream ? Sse(StreamBody()) : Json(Result());
        });
        await using var client = Build(handler);
        var parameters = new Dictionary<string, JsonElement> { ["future"] = JsonSerializer.Deserialize<JsonElement>(json) };
        var input = Input(); var before = input.ToJsonString();
        if (stream) await ConsumeAsync(client, input, parameters);
        else
        {
            var result = await InvokeAsync(client, input, parameters);
            Assert.Equal("native", result.Data.GetProperty("future").GetString());
            Assert.Equal("model-a", result.Metadata.UpstreamModel);
            Assert.Null(result.Metadata.Conversion);
        }
        Assert.Equal(before, input.ToJsonString());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), sent!["future"]));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>工具定义、结果、图片、签名、加密内容和未知字段原样进入正文。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativePipeline_PreservesOpaqueContent(bool stream)
    {
        var input = Input();
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.True(JsonNode.DeepEquals(input, body[Claude ? "messages" : "contents"]));
            Assert.Equal("opaque", body["future"]!["encrypted"]!.GetValue<string>());
            return stream ? Sse(StreamBody()) : Json(Result());
        });
        await using var client = Build(handler);
        var parameters = new Dictionary<string, JsonElement> { ["future"] = JsonSerializer.Deserialize<JsonElement>("{\"encrypted\":\"opaque\"}") };
        if (stream) await ConsumeAsync(client, input, parameters); else await InvokeAsync(client, input, parameters);
    }

    /// <summary>未知 SSE 事件及原始传输身份在 UTF-8 分片后仍然保留。</summary>
    [Fact]
    public async Task NativeStream_PreservesUnknownEventsAndMetadata()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse("event: vendor.future\nid: e1\nretry: 123\ndata: {\"future\":\"中文\"}\n\n" + StreamBody())));
        await using var client = Build(handler);
        var events = await ConsumeAsync(client, Input(), []);
        Assert.Equal("中文", events[0].Data.GetProperty("future").GetString());
        Assert.Equal("vendor.future", events[0].EventName); Assert.Equal("e1", events[0].EventId); Assert.Equal(123, events[0].RetryMilliseconds);
    }

    /// <summary>缺失供应商终态的流不能冒充完成。</summary>
    [Fact]
    public async Task NativeStream_RejectsTruncation()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse("data: {\"future\":1}\n\n")));
        await using var client = Build(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => ConsumeAsync(client, Input(), []));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>供应商业务错误作为完整原生事件返回，不删除未知错误详情。</summary>
    [Fact]
    public async Task NativeStream_PreservesErrorEvent()
    {
        var error = Claude ? "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"future\":false}}" : "{\"error\":{\"code\":429,\"future\":false}}";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse("data: " + error + "\n\n")));
        await using var client = Build(handler);
        var events = await ConsumeAsync(client, Input(), []);
        Assert.False(Assert.Single(events).Data.GetProperty("error").GetProperty("future").GetBoolean());
    }

    /// <summary>流式方法不接受普通 JSON 冒充 SSE。</summary>
    [Fact]
    public async Task NativeStream_RejectsWrongMediaType()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Result())));
        await using var client = Build(handler);
        await Assert.ThrowsAsync<LlmUpstreamException>(() => ConsumeAsync(client, Input(), []));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>HTTP 错误保留原文和状态，不自动重试。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativePipeline_PreservesHttpErrors(bool stream)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"error\":\"native\"}") }));
        await using var client = Build(handler);
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(async () =>
        {
            if (stream) await ConsumeAsync(client, Input(), []); else await InvokeAsync(client, Input(), []);
        });
        Assert.Contains("native", error.ResponseBody); Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode); Assert.Equal(1, handler.Calls);
    }

    /// <summary>公共字段冲突在任何 HTTP 调用之前失败。</summary>
    [Theory]
    [InlineData("model")]
    [InlineData("content")]
    public async Task NativePipeline_RejectsReservedFields(string field)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("不应调用上游。"));
        await using var client = Build(handler);
        var name = field == "content" ? Claude ? "messages" : "contents" : field;
        await Assert.ThrowsAsync<ArgumentException>(() => InvokeAsync(client, Input(), new() { [name] = JsonSerializer.SerializeToElement(0) }));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>取消不会发起请求。</summary>
    [Fact]
    public async Task NativePipeline_ObservesCancellation()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await using var client = Build(handler);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync(client, Input(), [], cancellation.Token));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>两次调用同一个 Session 始终使用同一个完整目标。</summary>
    [Fact]
    public async Task NativePipeline_PinsSessionAcrossClients()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var sessionLock = new TestSessionLock();
        using var handler = new StubHttpHandler((request, _) =>
        {
            Assert.Contains("native.invalid", request.RequestUri!.Host);
            return Task.FromResult(Json(Result()));
        });
        await using var first = Build(handler, cache, sessionLock);
        await using var second = Build(handler, cache, sessionLock);
        await InvokeAsync(first, Input(), []); await InvokeAsync(second, Input(), []);
        Assert.Equal(2, handler.Calls);
    }

    /// <summary>构建只依赖内存缓存与测试协调器的实际 SDK 客户端。</summary>
    private ILokiClient Build(StubHttpHandler handler, MemoryDistributedCache? cache = null, TestSessionLock? sessionLock = null)
    {
        var provider = Provider(new StubHttpClientFactory(handler));
        return new LokiClientBuilder().AddLLM(new LlmOptions
        {
            Providers = [provider], Credentials = [new("key", "bound-key")],
            SessionStore = new DistributedCacheLlmSessionStore(cache ?? new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), sessionLock ?? new()),
            Routes = [new LlmRoute { Model = "public", Targets = [new() { Id = "target", ProviderId = provider.Id, BaseAddress = new("https://native.invalid/custom/"), UpstreamModel = "model-a", CredentialId = "key", Protocols = Protocol }] }]
        }).Build();
    }
    /// <summary>创建包含原生不透明字段的输入。</summary>
    private JsonArray Input() => JsonNode.Parse(Claude
        ? """[{"role":"assistant","content":[{"type":"thinking","thinking":"摘要","signature":"opaque"},{"type":"tool_use","id":"call_1","name":"lookup","input":{"x":0}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"call_1","content":"结果"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"aGVsbG8="}}]}]"""
        : """[{"role":"model","parts":[{"functionCall":{"id":"call_1","name":"lookup","args":{"x":0}},"thoughtSignature":"opaque"}]},{"role":"user","parts":[{"functionResponse":{"id":"call_1","name":"lookup","response":{"x":false}}},{"inlineData":{"mimeType":"image/png","data":"aGVsbG8="}}]}]""")!.AsArray();
    /// <summary>执行完整原生调用。</summary>
    private async Task<LlmJsonResult> InvokeAsync(ILokiClient client, JsonArray input, Dictionary<string, JsonElement> parameters, CancellationToken? token = null)
        => Claude ? await client.LLM.ClaudeMessages.CreateAsync(new() { Model = "public", Messages = input, MaxTokens = 128, Parameters = parameters }, new() { SessionId = "native-session" }, token ?? TestContext.Current.CancellationToken)
            : await client.LLM.Gemini.GenerateAsync(new() { Model = "public", Contents = input, Parameters = parameters }, new() { SessionId = "native-session" }, token ?? TestContext.Current.CancellationToken);
    /// <summary>完整消费原生流，触发终态验证。</summary>
    private async Task<List<LlmJsonEvent>> ConsumeAsync(ILokiClient client, JsonArray input, Dictionary<string, JsonElement> parameters)
    {
        List<LlmJsonEvent> events = [];
        if (Claude)
            await foreach (var item in client.LLM.ClaudeMessages.StreamAsync(new() { Model = "public", Messages = input, MaxTokens = 128, Parameters = parameters }, new() { SessionId = "native-session" }, TestContext.Current.CancellationToken)) events.Add(item);
        else
            await foreach (var item in client.LLM.Gemini.StreamAsync(new() { Model = "public", Contents = input, Parameters = parameters }, new() { SessionId = "native-session" }, TestContext.Current.CancellationToken)) events.Add(item);
        return events;
    }
    /// <summary>最小成功 JSON，仅验证透传。</summary>
    private static string Result() => "{\"future\":\"native\"}";
    /// <summary>包含明确终态的最小供应商流。</summary>
    private string StreamBody() => Claude ? "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n" : "data: {\"candidates\":[{\"index\":0,\"finishReason\":\"STOP\"}]}\n\n";
    /// <summary>创建 JSON 测试响应。</summary>
    private static HttpResponseMessage Json(string data) => new(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "application/json") };
    /// <summary>创建按三字节分片的 SSE 测试响应。</summary>
    private static HttpResponseMessage Sse(string data)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(data)) };
        response.Content.Headers.ContentType = new("text/event-stream"); return response;
    }
}
