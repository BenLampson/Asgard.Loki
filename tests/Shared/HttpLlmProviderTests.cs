using Asgard.Loki.LLM;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Testing;

/// <summary>两种供应商分别执行相同的公共协议兼容性断言。</summary>
public abstract class HttpLlmProviderTests
{
    /// <summary>由具体测试程序集提供实际供应商实现。</summary>
    protected abstract ILlmProvider CreateProvider(IHttpClientFactory factory);

    /// <summary>实际目标地址和 Key 进入请求，未知结果字段完整保留。</summary>
    [Fact]
    public async Task CreateAsync_UsesTargetAddressAndCredential()
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("https://upstream.invalid/custom/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("bound-key", request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("real-model", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("opaque", body.RootElement.GetProperty("encrypted").GetString());
            return Json("{\"id\":\"r1\",\"status\":\"completed\",\"output\":[],\"future\":{\"value\":7}}");
        });
        var provider = CreateProvider(new StubHttpClientFactory(handler));
        var result = await ((IResponsesAdapter)provider).CreateAsync(
            new JsonObject { ["model"] = "real-model", ["encrypted"] = "opaque" }, Context(provider), TestContext.Current.CancellationToken);
        Assert.Equal("r1", result.Id);
        Assert.Equal(7, result.Data.GetProperty("future").GetProperty("value").GetInt32());
        Assert.Equal("alias", result.Metadata.PublicModel);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>SSE 解析不能依赖网络分块、单行 JSON 或已知事件名单。</summary>
    [Theory]
    [InlineData("text/event-stream")]
    [InlineData("Text/Event-Stream")]
    public async Task ResponsesStream_PreservesFragmentedMultilineAndUnknownEvents(string mediaType)
    {
        var body = ": keepalive\r\nevent: response.created\r\nid: e1\r\nretry: 1500\r\ndata: {\"type\":\"response.created\",\r\ndata: \"response\":{\"id\":\"r1\"}}\r\n\r\n"
            + "event: vendor.future\nid: e2\ndata: {\"type\":\"vendor.future\",\"value\":\"中文内容\"}\n\n"
            + "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"r1\",\"status\":\"completed\",\"usage\":{\"cached_tokens\":12}}}\n\n";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse(body, mediaType)));
        var provider = CreateProvider(new StubHttpClientFactory(handler));
        var events = await CollectAsync(((IResponsesAdapter)provider).StreamAsync(new JsonObject(), Context(provider), TestContext.Current.CancellationToken));
        Assert.Equal(3, events.Count);
        Assert.Equal("e1", events[0].EventId);
        Assert.Equal(1500, events[0].RetryMilliseconds);
        Assert.Equal("中文内容", events[1].Data.GetProperty("value").GetString());
        Assert.Equal(12, events[2].Data.GetProperty("response").GetProperty("usage").GetProperty("cached_tokens").GetInt32());
    }

    /// <summary>缺失 Responses 终态时不能报告成功。</summary>
    [Fact]
    public async Task ResponsesStream_RejectsTruncatedGeneration()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse("data: {\"type\":\"response.output_text.delta\",\"delta\":\"half\"}\n\n")));
        var provider = CreateProvider(new StubHttpClientFactory(handler));
        await Assert.ThrowsAsync<InvalidDataException>(() => CollectAsync(((IResponsesAdapter)provider).StreamAsync(new JsonObject(), Context(provider), TestContext.Current.CancellationToken)));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Chat Completions 分块中的工具内容与终止哨兵正确处理。</summary>
    [Fact]
    public async Task ChatStream_PreservesToolCallsAndDone()
    {
        var body = "data: {\"id\":\"c1\",\"choices\":[{\"delta\":{\"tool_calls\":[{\"id\":\"tool1\",\"function\":{\"arguments\":\"{\\\"a\\\":1}\"}}]}}]}\n\n"
            + "data: {\"id\":\"c1\",\"choices\":[],\"usage\":{\"total_tokens\":4}}\n\ndata: [DONE]\n\n";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse(body)));
        var provider = CreateProvider(new StubHttpClientFactory(handler));
        var events = await CollectAsync(((IChatCompletionsAdapter)provider).StreamAsync(new JsonObject(), Context(provider), TestContext.Current.CancellationToken));
        Assert.Equal(2, events.Count);
        Assert.Equal("tool1", events[0].Data.GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal(4, events[1].Data.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    /// <summary>上游错误事件作为完整协议事件返回。</summary>
    [Fact]
    public async Task ResponsesStream_PreservesFailureEvent()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse("data: {\"type\":\"response.failed\",\"response\":{\"id\":\"r\",\"status\":\"failed\",\"error\":{\"code\":\"failed\",\"future\":1}}}\n\n")));
        var provider = CreateProvider(new StubHttpClientFactory(handler));
        var events = await CollectAsync(((IResponsesAdapter)provider).StreamAsync(new JsonObject(), Context(provider), TestContext.Current.CancellationToken));
        Assert.Equal("failed", Assert.Single(events).Data.GetProperty("response").GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>错误正文与状态码传播，生成不会自动重发。</summary>
    [Fact]
    public async Task HttpFailure_PreservesBodyWithoutRetry()
    {
        const string error = "{\"error\":{\"code\":\"quota\",\"future\":1}}";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        { Content = new StringContent(error) }));
        var provider = CreateProvider(new StubHttpClientFactory(handler));
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => ((IResponsesAdapter)provider).CreateAsync(new JsonObject(), Context(provider), TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.Equal(error, failure.ResponseBody);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>流式调用意外返回普通 JSON 时也保留错误正文。</summary>
    [Fact]
    public async Task NonSseResponse_PreservesOriginalBody()
    {
        const string body = "{\"error\":{\"code\":\"not_streaming\",\"future\":1}}";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body)));
        var provider = CreateProvider(new StubHttpClientFactory(handler));
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => CollectAsync(((IResponsesAdapter)provider)
            .StreamAsync(new JsonObject(), Context(provider), TestContext.Current.CancellationToken)));
        Assert.Equal(body, failure.ResponseBody);
    }

    /// <summary>普通 Chat 调用使用独立端点并保留完整结果。</summary>
    [Fact]
    public async Task ChatCompletion_UsesItsOwnEndpointAndResult()
    {
        using var handler = new StubHttpHandler((request, _) =>
        {
            Assert.Equal("https://upstream.invalid/custom/chat/completions", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json("{\"id\":\"c1\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"你好\"}}],\"usage\":{\"total_tokens\":5},\"future\":true}"));
        });
        var provider = CreateProvider(new StubHttpClientFactory(handler));
        var result = await ((IChatCompletionsAdapter)provider).CompleteAsync(new JsonObject(), Context(provider), TestContext.Current.CancellationToken);
        Assert.Equal("你好", result.Data.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.True(result.Data.GetProperty("future").GetBoolean());
        Assert.Equal(5, result.Data.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    /// <summary>构造真实上游目标上下文。</summary>
    private static LlmAdapterContext Context(ILlmProvider provider) => new(new LlmTarget
    {
        Id = "target", ProviderId = provider.Id, BaseAddress = new("https://upstream.invalid/custom/"),
        UpstreamModel = "real-model", CredentialId = "key"
    }, new LlmCredential("key", "bound-key"), "alias", "session");
    /// <summary>用分段读取流模拟真实网络 SSE。</summary>
    private static HttpResponseMessage Sse(string body, string mediaType = "text/event-stream")
    {
        var content = new StreamContent(new FragmentedReadStream(body));
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
    /// <summary>创建无网络 JSON 响应。</summary>
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    /// <summary>完整枚举流，确保最后的断流错误也进入断言。</summary>
    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        List<T> items = [];
        await foreach (var item in source) items.Add(item);
        return items;
    }
}
