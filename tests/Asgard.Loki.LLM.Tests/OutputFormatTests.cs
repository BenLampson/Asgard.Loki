using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki;
using Xunit;
using static Asgard.Loki.LLM.Tests.OutputFormatFixture;

namespace Asgard.Loki.LLM.Tests;

/// <summary>独立原生请求覆盖格式许可/拒绝、完整与流式公开入口，不把本地结果称为真实格式效果。</summary>
public sealed class OutputFormatTests
{
    /// <summary>保持各协议原生 Schema 外壳和未知值差异，而不是序列化同一个通用格式模型。</summary>
    public static TheoryData<LlmProtocol, string, string, bool> Formats
    {
        get
        {
            TheoryData<LlmProtocol, string, string, bool> data = [];
            var windows = new (LlmProtocol Protocol, string Kind, string Json)[]
            {
                (LlmProtocol.ChatCompletions, "text", """{"response_format":{"type":"text"}}"""),
                (LlmProtocol.ChatCompletions, "json_object", """{"response_format":{"type":"json_object"}}"""),
                (LlmProtocol.ChatCompletions, "json_schema", """{"response_format":{"type":"json_schema","json_schema":{"name":"answer","strict":true,"schema":{"type":"object","additionalProperties":false,"future":9007199254740993}}}}"""),
                (LlmProtocol.Responses, "json_object", """{"text":{"format":{"type":"json_object"},"future":null}}"""),
                (LlmProtocol.Responses, "json_schema", """{"text":{"format":{"type":"json_schema","name":"answer","schema":{"type":"object"}}}}"""),
                (LlmProtocol.Responses, "json_schema", """{"response_format":{"type":"json_schema","json_schema":{"schema":{"type":"object"}}}}"""),
                (LlmProtocol.ClaudeMessages, "json_schema", """{"output_config":{"effort":"high","format":{"type":"json_schema","schema":{"type":"object"}}}}"""),
                (LlmProtocol.Gemini, "text", """{"generationConfig":{"responseMimeType":"text/plain"}}"""),
                (LlmProtocol.Gemini, "json_object", """{"generationConfig":{"responseMimeType":"application/json"}}"""),
                (LlmProtocol.Gemini, "json_schema", """{"generationConfig":{"responseMimeType":"application/json","responseJsonSchema":{"type":"object"}}}"""),
                (LlmProtocol.Gemini, "json_schema", """{"generationConfig":{"responseMimeType":"application/json","responseSchema":{"type":"OBJECT"}}}"""),
                (LlmProtocol.Gemini, "json_schema", """{"generationConfig":{"responseFormat":{"text":{"mimeType":"APPLICATION_JSON","schema":{"type":"object"}}}}}"""),
                (LlmProtocol.Gemini, "json_object", """{"generationConfig":{"responseFormat":{"text":{"mimeType":"APPLICATION_JSON"}}}}"""),
                (LlmProtocol.Gemini, "text", """{"generationConfig":{"responseFormat":{"text":{"mimeType":"TEXT_PLAIN"}}}}"""),
                (LlmProtocol.OllamaChat, "text", """{"format":""}"""),
                (LlmProtocol.OllamaChat, "json_object", """{"format":"json"}"""),
                (LlmProtocol.OllamaChat, "json_schema", """{"format":{"type":"object","properties":{"id":{"type":"integer"}}}}"""),
                (LlmProtocol.OllamaGenerate, "json_object", """{"format":"json"}"""),
                (LlmProtocol.OllamaGenerate, "json_schema", """{"format":{"type":"object"}}"""),
                (LlmProtocol.BedrockConverse, "json_schema", """{"outputConfig":{"textFormat":{"type":"json_schema","structure":{"jsonSchema":{"name":"answer","schema":"{\"type\":\"object\"}"}}}}}"""),
                (LlmProtocol.CohereChat, "json_object", """{"response_format":{"type":"json_object"}}"""),
                (LlmProtocol.CohereChat, "json_schema", """{"response_format":{"type":"json_object","schema":{"type":"object","required":["name"]}}}"""),
                (LlmProtocol.WorkersAiText, "json_object", """{"response_format":{"type":"json_object"}}"""),
                (LlmProtocol.WorkersAiText, "json_schema", """{"response_format":{"type":"json_schema","json_schema":{"type":"object"}}}""")
            };
            foreach (var (protocol, kind, json) in windows) foreach (var stream in new[] { false, true }) data.Add(protocol, kind, json, stream);
            return data;
        }
    }

    /// <summary>许可后的每个原生值保持不变，Schema 字符串、对象和未知值不互换。</summary>
    [Theory] [MemberData(nameof(Formats))]
    public async Task AllowedFormat_PreservesOriginalEnvelope(LlmProtocol protocol, string kind, string json, bool stream)
    {
        var provider = new OutputFormatProvider(); var store = WebSocketFixture.Store();
        await using var client = new LokiClientBuilder().AddLLM(Options(provider, store, Target(protocol, [kind]))).Build();
        await Invoke(client, protocol, stream, json);
        var sent = Assert.Single(provider.Calls);
        foreach (var field in JsonNode.Parse(json)!.AsObject()) Assert.True(JsonNode.DeepEquals(field.Value, sent[field.Key]));
        Assert.Equal(1, store.GetOrCreateCalls);
    }

    /// <summary>空列表拒绝显式输出格式，包括显式 text；未先建立新会话，也不调用任何供应商。</summary>
    [Theory] [MemberData(nameof(Formats))]
    public async Task DisallowedFormat_FailsBeforeBindingAndAdapter(LlmProtocol protocol, string kind, string json, bool stream)
    {
        var provider = new OutputFormatProvider(); var store = WebSocketFixture.Store();
        await using var client = new LokiClientBuilder().AddLLM(Options(provider, store, Target(protocol, []))).Build();
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => Invoke(client, protocol, stream, json));
        Assert.Contains("format-target", error.Message); Assert.Contains(kind, error.Message); Assert.Contains("$.", error.Message);
        Assert.Equal(0, store.GetOrCreateCalls); Assert.Empty(provider.Calls);
    }

    /// <summary>格式省略时不制造 text 默认值，工具 Schema 和未知业务字段也不等同于输出 Schema。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)]
    [InlineData(LlmProtocol.Gemini)] [InlineData(LlmProtocol.OllamaChat)] [InlineData(LlmProtocol.OllamaGenerate)]
    [InlineData(LlmProtocol.BedrockConverse)] [InlineData(LlmProtocol.CohereChat)] [InlineData(LlmProtocol.WorkersAiText)]
    public async Task OmittedFormat_DoesNotInventSupportOrInspectToolSchemas(LlmProtocol protocol)
    {
        const string json = """{"tools":[{"input_schema":{"type":"object"}}],"future":{"format":{"type":"json_schema"}},"output_config":{"effort":"high"},"text":{"verbosity":"low"}}""";
        var provider = new OutputFormatProvider();
        await using var client = new LokiClientBuilder().AddLLM(Options(provider, WebSocketFixture.Store(), Target(protocol, []))).Build();
        await Invoke(client, protocol, false, json); await Invoke(client, protocol, true, json);
        Assert.Equal(2, provider.Calls.Count);
        Assert.All(provider.Calls, sent => Assert.False(sent.ContainsKey("response_format")));
    }

    /// <summary>声明只影响本协议，未声明协议保留既有原生透传；不能把 Chat 限制套用到 Responses。</summary>
    [Fact]
    public async Task ProtocolDeclarations_AreIndependent()
    {
        var provider = new OutputFormatProvider();
        var target = Target(LlmProtocol.ChatCompletions, ["text", "json_object"]) with { Protocols = LlmProtocol.ChatCompletions | LlmProtocol.Responses };
        await using var client = new LokiClientBuilder().AddLLM(Options(provider, WebSocketFixture.Store(), target)).Build();
        await Assert.ThrowsAsync<NotSupportedException>(() => Invoke(client, LlmProtocol.ChatCompletions, false, """{"response_format":{"type":"json_schema"}}""", "chat"));
        await Invoke(client, LlmProtocol.Responses, false, """{"text":{"format":{"type":"json_schema","schema":{"type":"object"}}}}""", "responses");
        Assert.Single(provider.Calls);
    }

    /// <summary>坏类型/空值和冲突外壳不能被当作省略，未知格式也不能越过显式白名单。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions, "{\"response_format\":null}")]
    [InlineData(LlmProtocol.ChatCompletions, "{\"response_format\":{}}")]
    [InlineData(LlmProtocol.ChatCompletions, "{\"response_format\":{\"type\":1}}")]
    [InlineData(LlmProtocol.Responses, "{\"text\":{\"format\":null}}")]
    [InlineData(LlmProtocol.Responses, "{\"response_format\":null}")]
    [InlineData(LlmProtocol.ClaudeMessages, "{\"output_config\":{\"format\":[]}}")]
    [InlineData(LlmProtocol.OllamaChat, "{\"format\":null}")]
    [InlineData(LlmProtocol.Gemini, "{\"generationConfig\":{\"responseFormat\":{\"text\":{\"mimeType\":\"APPLICATION_JSON\"}},\"responseMimeType\":\"application/json\"}}")]
    [InlineData(LlmProtocol.Gemini, "{\"generationConfig\":{\"responseMimeType\":\"application/json\",\"responseSchema\":{},\"responseJsonSchema\":{}}}")]
    [InlineData(LlmProtocol.Gemini, "{\"generationConfig\":{\"responseMimeType\":\"text/plain\",\"responseSchema\":{}}}")]
    [InlineData(LlmProtocol.BedrockConverse, "{\"outputConfig\":{\"textFormat\":false}}")]
    public async Task MalformedExplicitFormat_FailsOnlyWithDeclaration(LlmProtocol protocol, string json)
    {
        var provider = new OutputFormatProvider(); var store = WebSocketFixture.Store();
        await using (var client = new LokiClientBuilder().AddLLM(Options(provider, store, Target(protocol, ["text", "json_object", "json_schema"]))).Build())
            await Assert.ThrowsAsync<ArgumentException>(() => Invoke(client, protocol, false, json));
        Assert.Equal(0, store.GetOrCreateCalls); Assert.Empty(provider.Calls);
        await using var legacy = new LokiClientBuilder().AddLLM(Options(provider, store, Target(protocol))).Build();
        await Invoke(legacy, protocol, false, json); Assert.Single(provider.Calls);
    }

    /// <summary>未知原生字符串不能碰巧与统一类别同名就被放行，MIME 也不等于统一格式枚举。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions, "{\"response_format\":{\"type\":\"yaml\"}}")]
    [InlineData(LlmProtocol.OllamaChat, "{\"format\":\"json_schema\"}")]
    [InlineData(LlmProtocol.Gemini, "{\"generationConfig\":{\"responseMimeType\":\"json_schema\"}}")]
    [InlineData(LlmProtocol.Gemini, "{\"generationConfig\":{\"responseFormat\":{\"text\":{\"mimeType\":\"application/json\"}}}}")]
    public async Task UnknownNativeFormat_IsNotADeclaredCategory(LlmProtocol protocol, string json)
    {
        var provider = new OutputFormatProvider(); var store = WebSocketFixture.Store();
        await using var client = new LokiClientBuilder().AddLLM(Options(provider, store, Target(protocol, ["text", "json_object", "json_schema"]))).Build();
        await Assert.ThrowsAsync<NotSupportedException>(() => Invoke(client, protocol, false, json));
        Assert.Empty(provider.Calls); Assert.Equal(0, store.GetOrCreateCalls);
    }

    /// <summary>只配置其他模态或省略文本 MIME 的空配置不能制造显式文本格式。</summary>
    [Theory]
    [InlineData("{\"generationConfig\":{\"responseFormat\":{\"audio\":{\"mimeType\":\"AUDIO_MP3\"}}}}")]
    [InlineData("{\"generationConfig\":{\"responseFormat\":{\"text\":{}}}}")]
    public async Task GeminiWithoutExplicitTextFormat_RemainsUnchanged(string json)
    {
        var provider = new OutputFormatProvider();
        await using var client = new LokiClientBuilder().AddLLM(Options(provider, WebSocketFixture.Store(), Target(LlmProtocol.Gemini, []))).Build();
        await Invoke(client, LlmProtocol.Gemini, false, json);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json)!["generationConfig"], Assert.Single(provider.Calls)["generationConfig"]));
    }

    /// <summary>两处已知 Responses 外壳分别受约束，不能通过另一个获准字段遮住不支持的 Schema。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ResponsesEnvelopes_AreCheckedIndependently(bool schemaAtRoot)
    {
        var root = new JsonObject
        {
            ["response_format"] = new JsonObject { ["type"] = schemaAtRoot ? "json_schema" : "json_object" },
            ["text"] = new JsonObject { ["format"] = new JsonObject { ["type"] = schemaAtRoot ? "json_object" : "json_schema" } }
        };
        var provider = new OutputFormatProvider();
        await using var client = new LokiClientBuilder().AddLLM(Options(provider, WebSocketFixture.Store(), Target(LlmProtocol.Responses, ["json_object"]))).Build();
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => Invoke(client, LlmProtocol.Responses, false, root.ToJsonString()));
        Assert.Contains(schemaAtRoot ? "$.response_format.type" : "$.text.format.type", error.Message); Assert.Empty(provider.Calls);
    }

    /// <summary>校验使用首次缓存等待前的请求快照，不能在等待时改成已获准的格式。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RequestMutationDuringSessionWait_CannotBypassPolicy(bool stream)
    {
        var provider = new OutputFormatProvider(); var store = WebSocketFixture.Store();
        var parameters = new Dictionary<string, JsonElement> { ["response_format"] = JsonSerializer.Deserialize<JsonElement>("""{"type":"json_schema"}""") };
        store.BeforeRead = _ => { parameters["response_format"] = JsonSerializer.Deserialize<JsonElement>("""{"type":"json_object"}"""); return Task.CompletedTask; };
        await using var client = new LokiClientBuilder().AddLLM(Options(provider, store, Target(LlmProtocol.ChatCompletions, ["json_object"]))).Build();
        var request = new ChatCompletionRequest { Model = "public", Messages = [], Parameters = parameters };
        async Task InvokeRequest()
        {
            if (stream) { await foreach (var _ in client.LLM.ChatCompletions.StreamAsync(request, WebSocketFixture.Call(), TestContext.Current.CancellationToken)) { } }
            else await client.LLM.ChatCompletions.CompleteAsync(request, WebSocketFixture.Call(), TestContext.Current.CancellationToken);
        }
        await Assert.ThrowsAsync<NotSupportedException>(InvokeRequest); Assert.Empty(provider.Calls); Assert.Equal(0, store.GetOrCreateCalls);
    }

    /// <summary>WebSocket 创建使用目标声明，拒绝时没有原生消息被发送；随后合法请求仍可使用原连接。</summary>
    [Fact]
    public async Task WebSocketCreate_ValidatesBeforeSend()
    {
        var provider = new StubResponsesWebSocketProvider();
        var target = WebSocketFixture.Target() with { AllowedOutputFormats = new Dictionary<LlmProtocol, IReadOnlyList<string>> { [LlmProtocol.Responses] = ["json_object"] } };
        await using var client = WebSocketFixture.Build(provider, targets: [target]);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", WebSocketFixture.Call(), TestContext.Current.CancellationToken);
        var invalid = JsonNode.Parse("""{"type":"response.create","text":{"format":{"type":"json_schema","schema":{"type":"object"}}}}""")!.AsObject();
        var before = invalid.ToJsonString();
        await Assert.ThrowsAsync<NotSupportedException>(async () => await connection.SendAsync(invalid, TestContext.Current.CancellationToken));
        Assert.Equal(before, invalid.ToJsonString()); Assert.Empty(Assert.Single(provider.Opened).Connection.Sent);
        var valid = JsonNode.Parse("""{"type":"response.create","text":{"format":{"type":"json_object"}}}""")!.AsObject();
        await connection.SendAsync(valid, TestContext.Current.CancellationToken);
        Assert.Single(Assert.Single(provider.Opened).Connection.Sent);
    }
}
