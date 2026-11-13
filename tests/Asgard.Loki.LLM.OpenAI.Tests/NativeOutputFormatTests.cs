using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>使用替代 HTTP 验证 DeepSeek 目标声明与实际原生请求，不宣称模型接受或输出效果。</summary>
public sealed class NativeOutputFormatTests
{
    /// <summary>Chat Schema 在 HTTP 工厂创建前失败；不会改成 JSON 模式后发送。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DeepSeekChatSchema_RejectsBeforeHttpFactory(bool stream)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("HTTP must not be called"));
        var factory = new OpenAiHttpFactory(handler);
        await using var sdk = new LokiClientBuilder().AddLLM(new LlmOptions
        {
            Providers = [new OpenAiLlmProvider(factory)], SessionStore = CompactionFixture.Store(), Credentials = [new("key-a", "test-only")],
            Routes = [new() { Model = "public", Targets = [Target(false)] }]
        }).Build();
        await Assert.ThrowsAsync<NotSupportedException>(() => Invoke(sdk, false, stream, "json_schema"));
        Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }

    /// <summary>许可后的 Chat JSON 与 Responses Schema 使用各自路径和原生格式外壳，不相互转换。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task DeclaredFormat_PreservesNativeHttpBody(bool responses, bool stream)
    {
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(responses ? "https://api.deepseek.com/responses" : "https://api.deepseek.com/v1/chat/completions", message.RequestUri!.AbsoluteUri);
            using var data = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            var root = data.RootElement;
            Assert.Equal("selected-model", root.GetProperty("model").GetString());
            Assert.Equal(stream, root.GetProperty("stream").GetBoolean());
            var format = responses ? root.GetProperty("text").GetProperty("format") : root.GetProperty("response_format");
            Assert.Equal(responses ? "json_schema" : "json_object", format.GetProperty("type").GetString());
            Assert.Equal("9007199254740993", root.GetProperty("future").GetRawText());
            Assert.False(root.TryGetProperty(responses ? "response_format" : "text", out _));
            var part = OpenAiHttpFixture.Golden("deepseek-compatible").GetProperty(responses ? "responses" : "chat");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream ? part.GetProperty("sse").GetString()! : part.GetProperty("result").GetRawText(), Encoding.UTF8, stream ? "text/event-stream" : "application/json")
            };
        });
        var factory = new OpenAiHttpFactory(handler);
        await using var sdk = new LokiClientBuilder().AddLLM(new LlmOptions
        {
            Providers = [new OpenAiLlmProvider(factory)], SessionStore = CompactionFixture.Store(), Credentials = [new("key-a", "test-only")],
            Routes = [new() { Model = "public", Targets = [Target(responses)] }]
        }).Build();
        await Invoke(sdk, responses, stream, responses ? "json_schema" : "json_object");
        Assert.Equal(1, handler.Calls); Assert.Single(factory.Clients);
    }

    /// <summary>声明不认证模型支持，实际原生拒绝完整返回一次，不重试或静默改格式。</summary>
    [Fact]
    public async Task UpstreamRejection_RemainsNativeWithoutDowngrade()
    {
        const string body = """{"error":{"message":"format unavailable for selected model","code":"native-format"}}""";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        var factory = new OpenAiHttpFactory(handler);
        await using var sdk = new LokiClientBuilder().AddLLM(new LlmOptions
        {
            Providers = [new OpenAiLlmProvider(factory)], SessionStore = CompactionFixture.Store(), Credentials = [new("key-a", "test-only")],
            Routes = [new() { Model = "public", Targets = [Target(true)] }]
        }).Build();
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, true, false, "json_schema"));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode); Assert.Equal(body, error.ResponseBody); Assert.Equal(1, handler.Calls);
    }

    /// <summary>固定完整目标与原生格式能力，供应商仍是已有 OpenAI 兼容适配器。</summary>
    private static LlmTarget Target(bool responses)
    {
        var protocol = responses ? LlmProtocol.Responses : LlmProtocol.ChatCompletions;
        return new()
        {
            Id = "deepseek", ProviderId = "openai", BaseAddress = new(responses ? "https://api.deepseek.com/" : "https://api.deepseek.com/v1/"),
            UpstreamModel = "selected-model", CredentialId = "key-a", Protocols = protocol,
            AllowedOutputFormats = new Dictionary<LlmProtocol, IReadOnlyList<string>> { [protocol] = responses ? ["text", "json_object", "json_schema"] : ["text", "json_object"] }
        };
    }

    /// <summary>实际通过 SDK，保留未知整数与 Schema，且调用前后的请求序列化相同。</summary>
    private static async Task Invoke(Abstractions.ILokiClient sdk, bool responses, bool stream, string kind)
    {
        var format = new JsonObject { ["type"] = kind };
        if (kind == "json_schema")
        {
            var definition = responses ? format : new JsonObject();
            definition["name"] = "answer"; definition["schema"] = new JsonObject { ["type"] = "object" };
            if (!responses) format["json_schema"] = definition;
        }
        var parameters = new Dictionary<string, JsonElement>
        {
            [responses ? "text" : "response_format"] = JsonSerializer.SerializeToElement(responses ? new JsonObject { ["format"] = format } : format),
            ["future"] = JsonSerializer.Deserialize<JsonElement>("9007199254740993")
        };
        var before = JsonSerializer.Serialize(parameters); var call = CompactionFixture.Call(); var token = TestContext.Current.CancellationToken;
        if (responses)
        {
            var request = new ResponseRequest { Model = "public", Input = JsonValue.Create("JSON please"), Parameters = parameters };
            if (stream) { await foreach (var _ in sdk.LLM.Responses.StreamAsync(request, call, token)) { } } else await sdk.LLM.Responses.CreateAsync(request, call, token);
        }
        else
        {
            var request = new ChatCompletionRequest { Model = "public", Messages = [new JsonObject { ["role"] = "user", ["content"] = "JSON please" }], Parameters = parameters };
            if (stream) { await foreach (var _ in sdk.LLM.ChatCompletions.StreamAsync(request, call, token)) { } } else await sdk.LLM.ChatCompletions.CompleteAsync(request, call, token);
        }
        Assert.Equal(before, JsonSerializer.Serialize(parameters));
    }
}
