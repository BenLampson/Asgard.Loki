using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.Tests;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Testing;

/// <summary>通过完整公共入口检查参数序列化，对两种供应商执行相同的保真契约。</summary>
public abstract class HttpLlmPayloadTests : HttpLlmProviderTests
{
    /// <summary>覆盖省略以外的零值、false、null、空容器、Unicode 与嵌套扩展字段。</summary>
    public static TheoryData<bool, bool, string, string> ParameterCases
    {
        get
        {
            TheoryData<bool, bool, string, string> data = [];
            foreach (var responses in new[] { false, true })
            foreach (var stream in new[] { false, true })
            {
                var cases = new Dictionary<string, string>
                {
                    ["temperature"] = "0", ["top_p"] = "1", ["seed"] = "0",
                    ["parallel_tool_calls"] = "false", ["tool_choice"] = "\"none\"",
                    ["store"] = "false", ["tools"] = "[]", ["future_null"] = "null",
                    ["future_object"] = "{}", ["future_array"] = "[]",
                    ["future_unicode"] = JsonSerializer.Serialize("中文\n\"引号\"\\路径"),
                    ["future_nested"] = "{\"items\":[0,false,null,{\"encrypted_content\":\"opaque-test-only\"}]}",
                    ["thinking"] = "{\"type\":\"enabled\",\"future\":null}",
                    ["stream"] = stream ? "false" : "true"
                };
                if (responses)
                {
                    cases["reasoning"] = "{\"effort\":\"low\"}";
                    cases["text"] = "{\"format\":{\"type\":\"json_schema\",\"name\":\"result\",\"strict\":true,\"schema\":{\"type\":\"object\"}}}";
                    cases["max_output_tokens"] = "1";
                }
                else
                {
                    cases["reasoning_effort"] = "\"low\"";
                    cases["response_format"] = "{\"type\":\"json_schema\",\"json_schema\":{\"name\":\"result\",\"strict\":true,\"schema\":{\"type\":\"object\"}}}";
                    cases["max_completion_tokens"] = "1";
                }
                foreach (var item in cases) data.Add(responses, stream, item.Key, item.Value);
            }
            return data;
        }
    }

    /// <summary>每个断言检查最终 HTTP JSON，而非只检查序列化辅助方法。</summary>
    [Theory]
    [MemberData(nameof(ParameterCases))]
    public async Task PublicPipeline_PreservesParameterValues(bool responses, bool stream, string name, string json)
    {
        JsonObject? sent = null;
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("https://wire.invalid/custom/" + (responses ? "responses" : "chat/completions"), request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-wire-key", request.Headers.Authorization!.Parameter);
            sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            return Response(responses, stream);
        });
        var cache = Cache();
        await using var client = Build(CreateProvider(new StubHttpClientFactory(handler)), cache);
        using var value = JsonDocument.Parse(json);
        var parameters = new Dictionary<string, JsonElement> { [name] = value.RootElement.Clone() };
        object original = responses ? new ResponseRequest { Model = "public", Input = JsonValue.Create("input"), Parameters = parameters }
            : new ChatCompletionRequest { Model = "public", Messages = [new JsonObject { ["role"] = "user", ["content"] = "input" }], Parameters = parameters };
        var before = JsonSerializer.SerializeToNode(original);
        await InvokeAsync(client, original, responses, stream);
        Assert.NotNull(sent);
        Assert.Equal("real-model", sent["model"]!.GetValue<string>());
        Assert.Equal(stream, sent["stream"]!.GetValue<bool>());
        Assert.True(sent.ContainsKey(name), "The parameter field was omitted.");
        if (name != "stream") Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), sent[name]), "The final JSON parameter value changed.");
        Assert.True(JsonNode.DeepEquals(before, JsonSerializer.SerializeToNode(original)), "The caller request was mutated.");
        Assert.False(sent.ContainsKey(responses ? "reasoning_effort" : "reasoning"));
        Assert.False(sent.ContainsKey(responses ? "response_format" : "text"));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>带多模态或工具结果的内容数组，以及推理不透明字段，不能被压成纯文本。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PublicPipeline_PreservesNativeToolAndReasoningItems(bool responses, bool stream)
    {
        var input = JsonNode.Parse(responses ? """
            [{"type":"reasoning","id":"rs-original","summary":[{"type":"summary_text","text":"摘要"}],"encrypted_content":"opaque-test-only"},
             {"type":"function_call","id":"item-123","call_id":"call-456","name":"lookup","arguments":"{\"id\":42}"},
             {"type":"function_call_output","call_id":"call-456","output":"{\"receipt\":\"合成结果\"}"}]
            """ : """
            [{"role":"user","content":[{"type":"text","text":"中文"},{"type":"image_url","image_url":{"url":"https://image.invalid/test.png"}}]},
             {"role":"assistant","content":null,"reasoning_content":"摘要","tool_calls":[{"id":"call-456","type":"function","function":{"name":"lookup","arguments":"{\"id\":42}"}}]},
             {"role":"tool","tool_call_id":"call-456","content":"{\"receipt\":\"合成结果\"}"}]
            """)!;
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.True(JsonNode.DeepEquals(input, body[responses ? "input" : "messages"]), "Native protocol items were changed or discarded.");
            return Response(responses, stream);
        });
        var cache = Cache();
        await using var client = Build(CreateProvider(new StubHttpClientFactory(handler)), cache);
        object request = responses ? new ResponseRequest { Model = "public", Input = input.DeepClone() }
            : new ChatCompletionRequest { Model = "public", Messages = input.DeepClone().AsArray() };
        await InvokeAsync(client, request, responses, stream);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>保留字段覆盖必须在网络请求及 Session 建立前失败。</summary>
    [Theory]
    [InlineData(false, false, "model")]
    [InlineData(false, true, "model")]
    [InlineData(false, false, "messages")]
    [InlineData(false, true, "messages")]
    [InlineData(true, false, "model")]
    [InlineData(true, true, "model")]
    [InlineData(true, false, "input")]
    [InlineData(true, true, "input")]
    [InlineData(true, false, "previous_response_id")]
    [InlineData(true, true, "previous_response_id")]
    public async Task PublicPipeline_RejectsReservedOverrides(bool responses, bool stream, string name)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("Network must not be invoked."));
        var cache = Cache();
        await using var client = Build(CreateProvider(new StubHttpClientFactory(handler)), cache);
        var parameters = new Dictionary<string, JsonElement> { [name] = JsonSerializer.SerializeToElement("hidden") };
        object request = responses ? new ResponseRequest { Model = "public", Parameters = parameters }
            : new ChatCompletionRequest { Model = "public", Messages = [], Parameters = parameters };
        await Assert.ThrowsAsync<ArgumentException>(() => InvokeAsync(client, request, responses, stream));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>可空核心字段按声明省略，而扩展字段中的显式 null 被保留。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicPipeline_DistinguishesOmittedAndExplicitNull(bool stream)
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.False(body.RootElement.TryGetProperty("input", out _));
            Assert.False(body.RootElement.TryGetProperty("previous_response_id", out _));
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("future_null").ValueKind);
            return Response(true, stream);
        });
        var cache = Cache();
        await using var client = Build(CreateProvider(new StubHttpClientFactory(handler)), cache);
        var request = new ResponseRequest { Model = "public", Parameters = new() { ["future_null"] = JsonSerializer.SerializeToElement<object?>(null) } };
        await InvokeAsync(client, request, true, stream);
    }

    /// <summary>统一通过公开方法调用，流式分支必须枚举到结束。</summary>
    private static async Task InvokeAsync(ILokiClient client, object request, bool responses, bool stream)
    {
        var call = new LlmCallOptions { SessionId = "wire-session" };
        var token = TestContext.Current.CancellationToken;
        if (responses)
        {
            if (stream) await foreach (var item in client.LLM.Responses.StreamAsync((ResponseRequest)request, call, token)) Assert.Equal("public", item.Metadata.PublicModel);
            else Assert.Equal("public", (await client.LLM.Responses.CreateAsync((ResponseRequest)request, call, token)).Metadata.PublicModel);
        }
        else
        {
            if (stream) await foreach (var item in client.LLM.ChatCompletions.StreamAsync((ChatCompletionRequest)request, call, token)) Assert.Equal("public", item.Metadata.PublicModel);
            else Assert.Equal("public", (await client.LLM.ChatCompletions.CompleteAsync((ChatCompletionRequest)request, call, token)).Metadata.PublicModel);
        }
    }

    /// <summary>缓存和锁都在本地，HTTP 也完全替代，不访问真实模型。</summary>
    private static MemoryDistributedCache Cache() => new(Options.Create(new MemoryDistributedCacheOptions()));

    /// <summary>完整公共入口及其独立的上游模型、地址、凭据配置。</summary>
    private static ILokiClient Build(ILlmProvider provider, MemoryDistributedCache cache) => new LokiClientBuilder().AddLLM(new LlmOptions
    {
        SessionStore = new DistributedCacheLlmSessionStore(cache, new TestSessionLock()), Providers = [provider],
        Credentials = [new("wire-key", "test-wire-key")],
        Routes = [new LlmRoute { Model = "public", Targets = [new LlmTarget
        { Id = "wire", ProviderId = provider.Id, BaseAddress = new("https://wire.invalid/custom/"), UpstreamModel = "real-model", CredentialId = "wire-key" }] }]
    }).Build();

    /// <summary>只构造最小协议终态；模型参数实际生效由独立真实联调证明。</summary>
    private static HttpResponseMessage Response(bool responses, bool stream)
    {
        var json = responses ? "{\"id\":\"r-wire\",\"status\":\"completed\",\"output\":[]}"
            : "{\"id\":\"c-wire\",\"choices\":[]}";
        var text = stream ? responses ? "data: {\"type\":\"response.completed\",\"response\":" + json + "}\n\n"
            : "data: " + json + "\n\ndata: [DONE]\n\n" : json;
        return new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, stream ? "text/event-stream" : "application/json") };
    }
}
