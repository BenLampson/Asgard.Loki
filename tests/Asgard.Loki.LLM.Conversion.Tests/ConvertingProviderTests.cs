using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Anthropic;
using Asgard.Loki.LLM.Google;
using Asgard.Loki.LLM.OpenAI;
using Asgard.Loki.LLM.Tests;
using Asgard.Loki.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>通过实际 SDK 公共入口、转换装饰器及原生 HTTP 适配器验证最终报文。</summary>
public sealed class ConvertingProviderTests
{
    /// <summary>十二个方向在完整模式和流式模式下均执行真实适配流水线。</summary>
    public static TheoryData<LlmProtocol, LlmProtocol, bool> Calls
    {
        get
        {
            TheoryData<LlmProtocol, LlmProtocol, bool> cases = [];
            foreach (var from in new[] { LlmProtocol.ChatCompletions, LlmProtocol.Responses, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini })
            foreach (var to in new[] { LlmProtocol.ChatCompletions, LlmProtocol.Responses, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini })
                if (from != to) { cases.Add(from, to, false); cases.Add(from, to, true); }
            return cases;
        }
    }

    /// <summary>断言最终地址、认证、模型映射和转换返回值，不能仅检查辅助序列化函数。</summary>
    [Theory]
    [MemberData(nameof(Calls))]
    public async Task PublicPipeline_ConvertsRequestAndResponse(LlmProtocol source, LlmProtocol upstream, bool stream)
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var path = upstream switch { LlmProtocol.ChatCompletions => "chat/completions", LlmProtocol.Responses => "responses", LlmProtocol.ClaudeMessages => "messages", _ => "models/model-a:" + (stream ? "streamGenerateContent?alt=sse" : "generateContent") };
            Assert.Equal("https://wire.invalid/v1/" + path, request.RequestUri!.AbsoluteUri);
            Assert.DoesNotContain("bound-key", request.RequestUri.AbsoluteUri);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            if (upstream != LlmProtocol.Gemini)
            {
                Assert.Equal("model-a", body["model"]!.GetValue<string>()); Assert.Equal(stream, body["stream"]!.GetValue<bool>());
            }
            else { Assert.False(body.ContainsKey("model")); Assert.False(body.ContainsKey("stream")); }
            Assert.Contains("lookup", body.ToJsonString()); Assert.Contains("call_1", body.ToJsonString());
            if (upstream is LlmProtocol.ChatCompletions or LlmProtocol.Responses) Assert.Equal("bound-key", request.Headers.Authorization!.Parameter);
            else Assert.Equal("bound-key", Assert.Single(request.Headers.GetValues(upstream == LlmProtocol.Gemini ? "x-goog-api-key" : "x-api-key")));
            if (!stream) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ConversionFixtures.Response(upstream).ToJsonString(), Encoding.UTF8, "application/json") };
            var sse = string.Concat(ConversionFixtures.Stream(upstream).Select(e => "data: " + e.ToJsonString() + "\n\n")) + (upstream == LlmProtocol.ChatCompletions ? "data: [DONE]\n\n" : "");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(sse)) };
            response.Content.Headers.ContentType = new("text/event-stream"); return response;
        });
        await using var client = Build(handler, source, upstream);
        var request = ConversionFixtures.Request(source); request["model"] = "public";
        var before = request.ToJsonString();
        if (stream)
        {
            var events = await StreamAsync(client, source, request);
            Assert.NotEmpty(events);
            Assert.All(events, e => { Assert.NotNull(e.Metadata.Conversion); Assert.Equal(upstream, e.Metadata.Conversion.UpstreamProtocol); });
            Assert.Contains(events, e => e.Data.GetRawText().Contains("lookup", StringComparison.Ordinal));
        }
        else
        {
            var result = await CompleteAsync(client, source, request);
            Assert.NotNull(result.Metadata.Conversion); Assert.Empty(result.Metadata.Conversion.Diagnostics);
            Assert.Equal(upstream, result.Metadata.Conversion.UpstreamProtocol); Assert.Equal(source, result.Metadata.Conversion.PublicProtocol);
            Assert.True(JsonNode.DeepEquals(ConversionFixtures.Response(upstream), JsonNode.Parse(result.Metadata.Conversion.UpstreamData.GetRawText())));
            Assert.Equal("public", result.Metadata.PublicModel); Assert.Equal("model-a", result.Metadata.UpstreamModel);
            Assert.Contains("lookup", result.Data.GetRawText());
        }
        Assert.Equal(before, request.ToJsonString()); Assert.Equal(1, handler.Calls);
    }

    /// <summary>未支持字段在真实 HTTP 发出前失败。</summary>
    [Fact]
    public async Task LossyRequest_DoesNotCallUpstream()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await using var client = Build(handler, LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages);
        var request = ConversionFixtures.Request(LlmProtocol.ChatCompletions); request["model"] = "public"; request["future"] = false;
        await Assert.ThrowsAsync<LlmConversionException>(() => CompleteAsync(client, LlmProtocol.ChatCompletions, request));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>转换策略或实际上游协议变化后，旧 Session 不能继续调用。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_RejectsChangedConversionBinding(bool changePolicy)
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var store = new DistributedCacheLlmSessionStore(cache, new TestSessionLock());
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ConversionFixtures.Response(LlmProtocol.ChatCompletions).ToJsonString(), Encoding.UTF8, "application/json") }));
        await using var first = Build(handler, LlmProtocol.ClaudeMessages, LlmProtocol.ChatCompletions, store);
        await using var second = Build(handler, LlmProtocol.ClaudeMessages, changePolicy ? LlmProtocol.ChatCompletions : LlmProtocol.Responses,
            store, new() { AllowLoss = changePolicy });
        var request = ConversionFixtures.Request(LlmProtocol.ClaudeMessages); request["model"] = "public";
        await CompleteAsync(first, LlmProtocol.ClaudeMessages, request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CompleteAsync(second, LlmProtocol.ClaudeMessages, request));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>同协议装饰器不做转换，不增加转换元数据或删除未知字段。</summary>
    [Fact]
    public async Task NativeBranch_PreservesRawResult()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"id\":\"r1\",\"future\":false}", Encoding.UTF8, "application/json") }));
        await using var client = Build(handler, LlmProtocol.ChatCompletions, LlmProtocol.ChatCompletions);
        var request = ConversionFixtures.Request(LlmProtocol.ChatCompletions); request["model"] = "public"; request["future"] = 0;
        var result = await CompleteAsync(client, LlmProtocol.ChatCompletions, request);
        Assert.Null(result.Metadata.Conversion); Assert.False(result.Data.GetProperty("future").GetBoolean());
    }

    /// <summary>调用者显式注册转换供应商；原生供应商不引用转换包。</summary>
    private static ILokiClient Build(StubHttpHandler handler, LlmProtocol source, LlmProtocol upstream,
        ILlmSessionStore? store = null, LlmConversionOptions? options = null)
    {
        var factory = new StubHttpClientFactory(handler);
        ILlmProvider native = upstream switch { LlmProtocol.ClaudeMessages => new AnthropicLlmProvider(factory), LlmProtocol.Gemini => new GoogleLlmProvider(factory), _ => new OpenAiLlmProvider(factory) };
        var provider = new ConvertingLlmProvider(native, upstream, "explicit-conversion", options);
        return new LokiClientBuilder().AddLLM(new LlmOptions
        {
            Providers = [provider], Credentials = [new("key", "bound-key")],
            SessionStore = store ?? new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock()),
            Routes = [new LlmRoute { Model = "public", Targets = [new() { Id = "target", ProviderId = provider.Id, BaseAddress = new("https://wire.invalid/v1/"), UpstreamModel = "model-a", CredentialId = "key", Protocols = source }] }]
        }).Build();
    }
    /// <summary>从公共 SDK 的各原生入口发起完整调用。</summary>
    private static async Task<LlmJsonResult> CompleteAsync(ILokiClient client, LlmProtocol source, JsonObject request)
    {
        var call = new LlmCallOptions { SessionId = "wire-session" }; var token = TestContext.Current.CancellationToken;
        return source switch
        {
            LlmProtocol.ChatCompletions => await client.LLM.ChatCompletions.CompleteAsync(request.Deserialize<ChatCompletionRequest>()!, call, token),
            LlmProtocol.Responses => await client.LLM.Responses.CreateAsync(request.Deserialize<ResponseRequest>()!, call, token),
            LlmProtocol.ClaudeMessages => await client.LLM.ClaudeMessages.CreateAsync(request.Deserialize<ClaudeMessageRequest>()!, call, token),
            _ => await client.LLM.Gemini.GenerateAsync(request.Deserialize<GeminiContentRequest>()!, call, token)
        };
    }
    /// <summary>消费所有公共流事件，不绕过供应商终态检查。</summary>
    private static async Task<List<LlmJsonEvent>> StreamAsync(ILokiClient client, LlmProtocol source, JsonObject request)
    {
        List<LlmJsonEvent> events = []; var call = new LlmCallOptions { SessionId = "wire-session" }; var token = TestContext.Current.CancellationToken;
        switch (source)
        {
            case LlmProtocol.ChatCompletions:
                await foreach (var item in client.LLM.ChatCompletions.StreamAsync(request.Deserialize<ChatCompletionRequest>()!, call, token)) events.Add(item); break;
            case LlmProtocol.Responses:
                await foreach (var item in client.LLM.Responses.StreamAsync(request.Deserialize<ResponseRequest>()!, call, token)) events.Add(item); break;
            case LlmProtocol.ClaudeMessages:
                await foreach (var item in client.LLM.ClaudeMessages.StreamAsync(request.Deserialize<ClaudeMessageRequest>()!, call, token)) events.Add(item); break;
            case LlmProtocol.Gemini:
                await foreach (var item in client.LLM.Gemini.StreamAsync(request.Deserialize<GeminiContentRequest>()!, call, token)) events.Add(item); break;
        }
        return events;
    }
}
