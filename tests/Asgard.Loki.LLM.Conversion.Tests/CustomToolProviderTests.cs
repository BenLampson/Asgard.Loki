using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>通过公共 SDK 和真实原生适配器验证 custom 完整调用、历史重放及上下文隔离。</summary>
public sealed class CustomToolProviderTests
{
    /// <summary>核对最终 HTTP 报文与响应元数据，第二次调用使用第一次返回的原生 custom 项。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public async Task PublicPipeline_RestoresAndReplays(LlmProtocol target)
    {
        string? previous = null; JsonObject? upstream = null;
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var wire = CustomToolFixtures.Parse(await request.Content!.ReadAsStringAsync(token));
            var name = CustomToolFixtures.Name(wire, target); Assert.NotEqual(previous, name);
            if (previous is null)
            {
                var expected = CustomToolFixtures.Bind(CustomToolFixtures.ExpectedRequest(target), name);
                if (target == LlmProtocol.Gemini) expected.Remove("model"); else expected["stream"] = false;
                CustomToolConversionTests.Equal(expected, wire);
            }
            else
            {
                Assert.DoesNotContain(previous, wire.ToJsonString());
                Assert.Contains("new_custom", wire.ToJsonString()); Assert.Contains("new_plain", wire.ToJsonString());
                if (target == LlmProtocol.Gemini)
                    Assert.Equal("arbitrary text", wire["contents"]![3]!["parts"]![0]!["functionResponse"]!["response"]!["output"]!.GetValue<string>());
            }
            upstream = CustomToolFixtures.Bind(CustomToolFixtures.Response(target), name);
            if (previous is not null) upstream = CustomToolFixtures.Parse(upstream.ToJsonString().Replace("new_custom", "second_custom", StringComparison.Ordinal).Replace("new_plain", "second_plain", StringComparison.Ordinal));
            previous = name; return Reply(upstream);
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, target, options: CustomToolFixtures.Options);
        var source = CustomToolFixtures.Request(); source["model"] = "public";
        var first = await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Responses, source);
        CustomToolConversionTests.Equal(CustomToolFixtures.ExpectedResponse(), JsonNode.Parse(first.Data.GetRawText()));
        CustomToolConversionTests.Equal(upstream, JsonNode.Parse(first.Metadata.Conversion!.UpstreamData.GetRawText()));
        var items = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "start" });
        // 对话文本自行重建；这里重点验证 SDK 返回的完整工具项含 id/status 可直接回放。
        foreach (var item in JsonNode.Parse(first.Data.GetRawText())!["output"]!.AsArray().Skip(1)) items.Add(item!.DeepClone());
        items.Add(CustomToolFixtures.Parse("""{"type":"custom_tool_call_output","call_id":"new_custom","output":"arbitrary text"}"""));
        items.Add(CustomToolFixtures.Parse("""{"type":"function_call_output","call_id":"new_plain","output":"{}"}"""));
        items.Add(new JsonObject { ["role"] = "user", ["content"] = "again" }); source["input"] = items;
        var second = await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Responses, source);
        Assert.Equal("second_custom", second.Data.GetProperty("output")[1].GetProperty("call_id").GetString());
        Assert.Equal(2, handler.Calls);
    }

    /// <summary>同一装饰器并发请求乱序返回，映射不共享也不会按响应顺序错绑。</summary>
    [Fact]
    public async Task ConcurrentCalls_KeepIndependentContexts()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var wire = CustomToolFixtures.Parse(await request.Content!.ReadAsStringAsync(token));
            var index = Interlocked.Increment(ref calls);
            if (index == 1) await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token); else entered.SetResult();
            return Reply(CustomToolFixtures.Bind(CustomToolFixtures.Response(LlmProtocol.ChatCompletions), CustomToolFixtures.Name(wire, LlmProtocol.ChatCompletions)));
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, LlmProtocol.ChatCompletions, options: CustomToolFixtures.Options);
        async Task<string?> Call(string tool)
        {
            var source = CustomToolFixtures.Request(); source["model"] = "public"; source["tools"]![0]!["name"] = tool; source["tool_choice"]!["name"] = tool;
            var response = await client.LLM.Responses.CreateAsync(source.Deserialize<ResponseRequest>()!, new() { SessionId = tool }, TestContext.Current.CancellationToken);
            return response.Data.GetProperty("output")[1].GetProperty("name").GetString();
        }
        Assert.Equal(["first", "second"], await Task.WhenAll(Call("first"), Call("second"))); Assert.Equal(2, handler.Calls);
    }

    /// <summary>未实现的 custom 流式和 grammar 在原生适配器发出任何请求前失败。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions, false)] [InlineData(LlmProtocol.ClaudeMessages, false)] [InlineData(LlmProtocol.Gemini, false)]
    [InlineData(LlmProtocol.ChatCompletions, true)] [InlineData(LlmProtocol.ClaudeMessages, true)] [InlineData(LlmProtocol.Gemini, true)]
    public async Task UnsupportedCall_DoesNotSend(LlmProtocol target, bool grammar)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, target, options: CustomToolFixtures.Options with { AllowLoss = true });
        var source = CustomToolFixtures.Request(); source["model"] = "public";
        if (grammar)
        {
            source["tools"]![0]!["format"] = CustomToolFixtures.Parse("""{"type":"grammar","syntax":"regex","definition":".*"}""");
            await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Responses, source));
        }
        else await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.StreamAsync(client, LlmProtocol.Responses, source));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>内存 HTTP 响应，不读取真实 Key 或调用上游。</summary>
    private static HttpResponseMessage Reply(JsonObject value) => new(HttpStatusCode.OK) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
}
