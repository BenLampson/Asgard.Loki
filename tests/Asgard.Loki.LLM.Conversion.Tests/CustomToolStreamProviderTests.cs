using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>公共 SDK、转换装饰器和原生 SSE 解析器共同验证 custom 流，HTTP 全部由内存处理器替代。</summary>
public sealed class CustomToolStreamProviderTests
{
    /// <summary>完整事件金样经过分片 SSE 仍保持一致，完成项连同消息元数据可直接重放。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions, false)] [InlineData(LlmProtocol.ClaudeMessages, false)] [InlineData(LlmProtocol.Gemini, false)]
    [InlineData(LlmProtocol.ChatCompletions, true)] [InlineData(LlmProtocol.ClaudeMessages, true)] [InlineData(LlmProtocol.Gemini, true)]
    public async Task PublicStream_MatchesGoldenAndReplays(LlmProtocol target, bool length)
    {
        var calls = 0; JsonObject[] original = []; string? oldName = null;
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var wire = CustomToolFixtures.Parse(await request.Content!.ReadAsStringAsync(token)); var name = CustomToolFixtures.Name(wire, target);
            if (++calls == 1)
            {
                var expected = CustomToolFixtures.Bind(CustomToolFixtures.ExpectedRequest(target), name);
                if (target == LlmProtocol.Gemini) expected.Remove("model"); else expected["stream"] = true;
                CustomToolConversionTests.Equal(expected, wire);
                Assert.Contains(target == LlmProtocol.Gemini ? "streamGenerateContent" : target == LlmProtocol.ClaudeMessages ? "messages" : "chat/completions", request.RequestUri!.AbsoluteUri);
                original = CustomToolStreamFixtures.Source(target, name); oldName = name;
                if (length)
                {
                    if (target == LlmProtocol.ChatCompletions) original[^2]["choices"]![0]!["finish_reason"] = "length";
                    else if (target == LlmProtocol.ClaudeMessages) original[^2]["delta"]!["stop_reason"] = "max_tokens";
                    else original[^1]["candidates"]![0]!["finishReason"] = "MAX_TOKENS";
                }
                return Sse(target, original);
            }
            Assert.NotEqual(oldName, name); Assert.DoesNotContain(oldName!, wire.ToJsonString());
            Assert.Contains("new_custom", wire.ToJsonString()); Assert.Contains("new_plain", wire.ToJsonString());
            var native = CustomToolFixtures.Bind(CustomToolFixtures.Response(target), name);
            native = CustomToolFixtures.Parse(native.ToJsonString().Replace("new_custom", "second_custom", StringComparison.Ordinal).Replace("new_plain", "second_plain", StringComparison.Ordinal));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(native.ToJsonString(), Encoding.UTF8, "application/json") };
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, target, options: CustomToolFixtures.Options);
        var source = CustomToolFixtures.Request(); source["model"] = "public"; var before = source.ToJsonString();
        var output = await ConvertingProviderTests.StreamAsync(client, LlmProtocol.Responses, source);
        var expectedEvents = CustomToolStreamFixtures.Expected();
        if (length)
        {
            foreach (var e in expectedEvents.Where(e => Type(e) == "response.output_item.done")) e["item"]!["status"] = "incomplete";
            expectedEvents[^1]["type"] = "response.incomplete"; var response = expectedEvents[^1]["response"]!;
            response["status"] = "incomplete"; response["incomplete_details"] = CustomToolFixtures.Parse("""{"reason":"max_output_tokens"}""");
            foreach (var item in response["output"]!.AsArray()) item!["status"] = "incomplete";
        }
        Assert.Equal(expectedEvents.Length, output.Count);
        for (var i = 0; i < output.Count; i++)
        {
            CustomToolConversionTests.Equal(expectedEvents[i], JsonNode.Parse(output[i].Data.GetRawText()));
            Assert.Equal(Type(expectedEvents[i]), output[i].EventName); Assert.Null(output[i].EventId); Assert.Null(output[i].RetryMilliseconds);
            Assert.Equal(target, output[i].Metadata.Conversion!.UpstreamProtocol);
            Assert.Contains(original, e => JsonNode.DeepEquals(e, JsonNode.Parse(output[i].Metadata.Conversion!.UpstreamData.GetRawText())));
        }
        Assert.Equal(before, source.ToJsonString());
        if (!length)
        {
            var history = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "start" });
            foreach (var item in JsonNode.Parse(output[^1].Data.GetRawText())!["response"]!["output"]!.AsArray()) history.Add(item!.DeepClone());
            history.Add(CustomToolFixtures.Parse("""{"type":"custom_tool_call_output","call_id":"new_custom","output":"free text"}"""));
            history.Add(CustomToolFixtures.Parse("""{"type":"function_call_output","call_id":"new_plain","output":"{}"}"""));
            history.Add(new JsonObject { ["role"] = "user", ["content"] = "again" }); source["input"] = history;
            var replayBefore = source.ToJsonString();
            var replay = await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Responses, source);
            Assert.Equal("second_custom", replay.Data.GetProperty("output")[1].GetProperty("call_id").GetString());
            Assert.Equal(replayBefore, source.ToJsonString());
        }
        Assert.Equal(length ? 1 : 2, handler.Calls);
    }

    /// <summary>真实原生解析路径发生截断、坏封装或未知工具时，不能重试或发出成功终态。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions, 0)] [InlineData(LlmProtocol.ClaudeMessages, 0)] [InlineData(LlmProtocol.Gemini, 0)]
    [InlineData(LlmProtocol.ChatCompletions, 1)] [InlineData(LlmProtocol.ClaudeMessages, 1)] [InlineData(LlmProtocol.Gemini, 1)]
    [InlineData(LlmProtocol.ChatCompletions, 2)] [InlineData(LlmProtocol.ClaudeMessages, 2)] [InlineData(LlmProtocol.Gemini, 2)]
    public async Task PublicStream_FailureHasNoSuccessfulTerminal(LlmProtocol target, int mutation)
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var wire = CustomToolFixtures.Parse(await request.Content!.ReadAsStringAsync(token));
            var source = CustomToolStreamFixtures.Source(target, mutation == 2 ? "wrong_context" : CustomToolFixtures.Name(wire, target)).ToList();
            if (mutation == 0) source.RemoveAt(target == LlmProtocol.ChatCompletions ? source.Count - 2 : source.Count - 1);
            if (mutation == 1) CustomToolStreamTests.SetArguments(source.ToArray(), target, "{\"input\":false}");
            return Sse(target, source, includeDone: mutation != 0);
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, target, options: CustomToolFixtures.Options with { AllowLoss = true });
        var request = CustomToolFixtures.Request(); request["model"] = "public"; var output = new List<ResponseEvent>();
        var failure = await Record.ExceptionAsync(async () =>
        {
            await foreach (var e in client.LLM.Responses.StreamAsync(request.Deserialize<ResponseRequest>()!, new() { SessionId = "failure" }, TestContext.Current.CancellationToken)) output.Add(e);
        });
        Assert.NotNull(failure); Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(output, e => e.Data.GetProperty("type").GetString() is "response.completed" or "response.incomplete");
    }

    /// <summary>用分片内存流模拟 SSE 身份；转换不能把这些源事件 ID 当作目标身份。</summary>
    private static HttpResponseMessage Sse(LlmProtocol target, IEnumerable<JsonObject> events, bool includeDone = true)
    {
        var body = string.Concat(events.Select((e, i) => $"id: source-{i}\nretry: 123\n" + (e["type"] is null ? "" : "event: " + Type(e) + "\n") + "data: " + e.ToJsonString() + "\n\n"));
        if (target == LlmProtocol.ChatCompletions && includeDone) body += "data: [DONE]\n\n";
        var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(body)) };
        result.Content.Headers.ContentType = new("text/event-stream"); return result;
    }
    private static string Type(JsonObject e) => e["type"]!.GetValue<string>();
}
