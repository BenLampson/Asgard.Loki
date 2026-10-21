using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>namespace 工具经公共 SDK、真实原生适配器和内存 HTTP/SSE 的端到端协议验证。</summary>
public sealed class NamespacedToolProviderTests
{
    /// <summary>完整/流式入口核对全部最终报文和事件，完成项随后带原 namespace 及可选结果元数据重放。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions, false)] [InlineData(LlmProtocol.ClaudeMessages, false)] [InlineData(LlmProtocol.Gemini, false)]
    [InlineData(LlmProtocol.ChatCompletions, true)] [InlineData(LlmProtocol.ClaudeMessages, true)] [InlineData(LlmProtocol.Gemini, true)]
    public async Task PublicPipeline_ConvertsAndReplays(LlmProtocol target, bool stream)
    {
        var calls = 0; string? oldName = null;
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var wire = CustomToolFixtures.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("bound-key", target is LlmProtocol.ChatCompletions ? request.Headers.Authorization!.Parameter
                : Assert.Single(request.Headers.GetValues(target == LlmProtocol.Gemini ? "x-goog-api-key" : "x-api-key")));
            var name = NamespacedToolFixtures.Name(wire, target, 2); Assert.NotEqual(oldName, name);
            if (++calls == 1)
            {
                var expected = NamespacedToolFixtures.Bind(NamespacedToolFixtures.ExpectedRequest(target), wire, target);
                if (target == LlmProtocol.Gemini) expected.Remove("model"); else expected["stream"] = stream;
                CustomToolConversionTests.Equal(expected, wire);
                if (stream) { oldName = name; return Sse(target, NamespacedToolFixtures.Stream(wire, target)); }
            }
            else
            {
                Assert.DoesNotContain(oldName!, wire.ToJsonString()); Assert.DoesNotContain("\"namespace\":", wire.ToJsonString());
                Assert.Contains(stream ? "new_custom" : "new_crm", wire.ToJsonString());
            }
            oldName = name;
            var response = NamespacedToolFixtures.Bind(NamespacedToolFixtures.Response(target), wire, target);
            if (calls > 1) response = CustomToolFixtures.Parse(response.ToJsonString().Replace("new_", "second_", StringComparison.Ordinal));
            return Json(response);
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, target, options: NamespacedToolFixtures.Options);
        var source = NamespacedToolFixtures.Request(); source["model"] = "public"; var before = source.ToJsonString(); JsonArray history;
        if (stream)
        {
            var events = await ConvertingProviderTests.StreamAsync(client, LlmProtocol.Responses, source); var expected = NamespacedToolFixtures.ExpectedStream();
            Assert.Equal(expected.Length, events.Count);
            for (var i = 0; i < events.Count; i++)
            {
                CustomToolConversionTests.Equal(expected[i], JsonNode.Parse(events[i].Data.GetRawText())); Assert.Equal(expected[i]["type"]!.GetValue<string>(), events[i].EventName);
                Assert.Equal(target, events[i].Metadata.Conversion!.UpstreamProtocol); Assert.Empty(events[i].Metadata.Conversion!.Diagnostics);
            }
            history = JsonNode.Parse(events[^1].Data.GetRawText())!["response"]!["output"]!.DeepClone().AsArray();
        }
        else
        {
            var result = await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Responses, source);
            CustomToolConversionTests.Equal(NamespacedToolFixtures.ExpectedResponse(), JsonNode.Parse(result.Data.GetRawText())); Assert.Empty(result.Metadata.Conversion!.Diagnostics);
            Assert.Contains(oldName!, result.Metadata.Conversion.UpstreamData.GetRawText());
            history = JsonNode.Parse(result.Data.GetRawText())!["output"]!.DeepClone().AsArray();
        }
        Assert.Equal(before, source.ToJsonString());
        AddResults(history); history.Insert(0, new JsonObject { ["role"] = "user", ["content"] = "start" }); source["input"] = history;
        var replayBefore = source.ToJsonString(); var second = await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Responses, source);
        Assert.Equal("second_crm", second.Data.GetProperty("output")[1].GetProperty("call_id").GetString());
        Assert.Equal("crm", second.Data.GetProperty("output")[1].GetProperty("namespace").GetString());
        Assert.Equal(replayBefore, source.ToJsonString()); Assert.Equal(2, handler.Calls);
    }

    /// <summary>组描述在完整和流式入口均需显式损耗许可，默认在网络之前失败。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions, false)] [InlineData(LlmProtocol.ClaudeMessages, false)] [InlineData(LlmProtocol.Gemini, false)]
    [InlineData(LlmProtocol.ChatCompletions, true)] [InlineData(LlmProtocol.ClaudeMessages, true)] [InlineData(LlmProtocol.Gemini, true)]
    public async Task DescriptionLoss_FailsBeforeHttp(LlmProtocol target, bool stream)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, target, options: NamespacedToolFixtures.Options);
        var source = NamespacedToolFixtures.Request(); source["model"] = "public"; source["tools"]![2]!["description"] = "policy";
        if (stream) await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.StreamAsync(client, LlmProtocol.Responses, source));
        else await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Responses, source));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>经过审阅的描述损耗在 SDK 元数据中保留诊断，不能把已删除的组描述伪装成函数描述。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions, false)] [InlineData(LlmProtocol.ClaudeMessages, false)] [InlineData(LlmProtocol.Gemini, false)]
    [InlineData(LlmProtocol.ChatCompletions, true)] [InlineData(LlmProtocol.ClaudeMessages, true)] [InlineData(LlmProtocol.Gemini, true)]
    public async Task DescriptionConsent_KeepsDiagnostics(LlmProtocol target, bool stream)
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var wire = CustomToolFixtures.Parse(await request.Content!.ReadAsStringAsync(token)); Assert.DoesNotContain("policy", wire.ToJsonString());
            return stream ? Sse(target, NamespacedToolFixtures.Stream(wire, target)) : Json(NamespacedToolFixtures.Bind(NamespacedToolFixtures.Response(target), wire, target));
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, target, options: NamespacedToolFixtures.Options with { AllowLoss = true });
        var source = NamespacedToolFixtures.Request(); source["model"] = "public"; source["tools"]![2]!["description"] = "policy";
        var metadata = stream ? (await ConvertingProviderTests.StreamAsync(client, LlmProtocol.Responses, source))[^1].Metadata
            : (await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Responses, source)).Metadata;
        var diagnostic = Assert.Single(metadata.Conversion!.Diagnostics); Assert.Equal("namespace_description_not_representable", diagnostic.Code);
        Assert.Equal("$.tools[2].description", diagnostic.Path); Assert.Equal(1, handler.Calls);
    }

    /// <summary>返回调用 ID 与 namespace 跨并发请求隔离，响应乱序不能串用映射。</summary>
    [Fact]
    public async Task ConcurrentNamespaces_KeepLocalIdentity()
    {
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var entered = 0;
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var wire = CustomToolFixtures.Parse(await request.Content!.ReadAsStringAsync(token));
            if (Interlocked.Increment(ref entered) == 1) await both.Task.WaitAsync(TimeSpan.FromSeconds(5), token); else both.SetResult();
            return Json(NamespacedToolFixtures.Bind(NamespacedToolFixtures.Response(LlmProtocol.ChatCompletions), wire, LlmProtocol.ChatCompletions));
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, LlmProtocol.ChatCompletions, options: NamespacedToolFixtures.Options);
        async Task<string?> Call(string scope)
        {
            var request = NamespacedToolFixtures.Request(); request["model"] = "public"; request["tools"]![2]!["name"] = scope;
            var result = await client.LLM.Responses.CreateAsync(request.Deserialize<ResponseRequest>()!, new() { SessionId = scope }, TestContext.Current.CancellationToken);
            return result.Data.GetProperty("output")[1].GetProperty("namespace").GetString();
        }
        Assert.Equal(["first", "second"], await Task.WhenAll(Call("first"), Call("second"))); Assert.Equal(2, handler.Calls);
    }

    /// <summary>只有 namespace 普通函数的完整/流式 SDK 调用不依赖 custom 选项。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions, false)] [InlineData(LlmProtocol.ClaudeMessages, false)] [InlineData(LlmProtocol.Gemini, false)]
    [InlineData(LlmProtocol.ChatCompletions, true)] [InlineData(LlmProtocol.ClaudeMessages, true)] [InlineData(LlmProtocol.Gemini, true)]
    public async Task FunctionOnlyPipeline_HasNoCustomDependency(LlmProtocol target, bool stream)
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var wire = CustomToolFixtures.Parse(await request.Content!.ReadAsStringAsync(token));
            return stream ? Sse(target, NamespacedToolFixtures.FunctionStream(wire, target)) : Json(NamespacedToolFixtures.FunctionResponse(wire, target));
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, target, options: NamespacedToolFixtures.Options with { EnableCustomToolConversion = false });
        var source = NamespacedToolConversionTests.FunctionsOnly(); source["model"] = "public"; JsonObject response;
        if (stream)
        {
            var events = await ConvertingProviderTests.StreamAsync(client, LlmProtocol.Responses, source);
            Assert.DoesNotContain(events, e => e.Data.GetProperty("type").GetString()!.StartsWith("response.custom_tool_call_input.", StringComparison.Ordinal));
            response = JsonNode.Parse(events[^1].Data.GetRawText())!["response"]!.AsObject();
        }
        else response = JsonNode.Parse((await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Responses, source)).Data.GetRawText())!.AsObject();
        Assert.Equal("function_call", response["output"]![1]!["type"]!.GetValue<string>()); Assert.Equal("crm", response["output"]![1]!["namespace"]!.GetValue<string>());
        Assert.Equal("{\"n\":7}", response["output"]![1]!["arguments"]!.GetValue<string>()); Assert.Equal("support", response["output"]![2]!["namespace"]!.GetValue<string>());
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>空结束流不能发出成功的 namespace 最终快照，SDK 不重试或切换目标。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public async Task TruncatedStream_HasNoSuccessfulTerminal(LlmProtocol target)
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var wire = CustomToolFixtures.Parse(await request.Content!.ReadAsStringAsync(token)); var events = NamespacedToolFixtures.Stream(wire, target).ToList();
            events.RemoveAt(target == LlmProtocol.ChatCompletions ? events.Count - 2 : events.Count - 1); return Sse(target, events, complete: false);
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, target, options: NamespacedToolFixtures.Options);
        var source = NamespacedToolFixtures.Request(); source["model"] = "public"; var returned = new List<ResponseEvent>();
        var failure = await Record.ExceptionAsync(async () =>
        { await foreach (var e in client.LLM.Responses.StreamAsync(source.Deserialize<ResponseRequest>()!, new() { SessionId = "truncated" }, TestContext.Current.CancellationToken)) returned.Add(e); });
        Assert.NotNull(failure); Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(returned, e => e.Data.GetProperty("type").GetString() is "response.completed" or "response.incomplete");
    }

    /// <summary>结果元数据只为校验身份，关联仍使用原始 call_id。</summary>
    private static void AddResults(JsonArray history)
    {
        foreach (var item in history.OfType<JsonObject>().Where(i => i.ContainsKey("call_id")).ToArray())
        {
            var custom = item["type"]!.GetValue<string>() == "custom_tool_call";
            var result = new JsonObject { ["type"] = custom ? "custom_tool_call_output" : "function_call_output", ["call_id"] = item["call_id"]!.DeepClone(), ["output"] = custom ? "text" : "{}" };
            if (!custom) { result["name"] = item["name"]!.DeepClone(); if (item["namespace"] is not null) result["namespace"] = item["namespace"]!.DeepClone(); }
            history.Add(result);
        }
        history.Add(new JsonObject { ["role"] = "user", ["content"] = "again" });
    }
    /// <summary>分片 SSE 只使用本地内容，保留原生协议的终止方式。</summary>
    private static HttpResponseMessage Sse(LlmProtocol target, IEnumerable<JsonObject> events, bool complete = true)
    {
        var body = string.Concat(events.Select(e => "data: " + e.ToJsonString() + "\n\n"));
        if (target == LlmProtocol.ChatCompletions && complete) body += "data: [DONE]\n\n";
        var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(body)) }; result.Content.Headers.ContentType = new("text/event-stream"); return result;
    }
    private static HttpResponseMessage Json(JsonObject value) => new(HttpStatusCode.OK) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
}
