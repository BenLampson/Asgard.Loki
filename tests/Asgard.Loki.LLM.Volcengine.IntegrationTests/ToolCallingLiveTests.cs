using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Volcengine.IntegrationTests;

/// <summary>验证工具选择、参数、流式增量与工具结果回填，真实工具仅为本地合成函数。</summary>
[Trait("Suite", "Parameters")]
[Trait("Scenario", "Tools")]
[Collection("Volcengine live")]
public sealed class ToolCallingLiveTests(ITestOutputHelper output)
{
    /// <summary>Chat 使用嵌套工具定义和 tool_call_id 回传结果。</summary>
    [Theory(Skip = "Enable live integration explicitly.", SkipType = typeof(LiveTestGate), SkipUnless = nameof(LiveTestGate.IsEnabled))]
    [InlineData(false)]
    [InlineData(true)]
    public Task ChatTools_RoundTripRealFunctionResults(bool stream)
        => LiveScenario.RunAsync($"chat-tools-{stream}", output, async scenario =>
        {
            var request = ParameterData.Chat("Use loki_get_receipt for order-42. After the tool result, answer with its receipt only.", ToolParameters(false, stream));
            var session = LiveScenario.Call();
            JsonObject assistant;
            if (stream)
            {
                var events = await scenario.ChatStreamAsync(request, session);
                assistant = AssembleChatTool(events);
            }
            else
            {
                var response = await scenario.ChatAsync(request, session);
                Assert.Equal("tool_calls", response.Data.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
                assistant = JsonNode.Parse(response.Data.GetProperty("choices")[0].GetProperty("message").GetRawText())!.AsObject();
            }

            var calls = assistant["tool_calls"]!.AsArray();
            Assert.Single(calls);
            var function = calls[0]!["function"]!;
            ParameterData.AssertTool(function["name"]!.GetValue<string>(), function["arguments"]!.GetValue<string>());
            var callId = calls[0]!["id"]!.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(callId));
            var receipt = "receipt-" + Guid.NewGuid().ToString("N");
            var followUp = ParameterData.Chat("unused", ToolParameters(false, false, true)) with
            {
                Messages =
                [
                    request.Messages[0]!.DeepClone(), assistant.DeepClone(),
                    new JsonObject { ["role"] = "tool", ["tool_call_id"] = callId, ["content"] = JsonSerializer.Serialize(new { receipt }) }
                ]
            };
            var final = await scenario.ChatAsync(followUp, session);
            var finalMessage = final.Data.GetProperty("choices")[0].GetProperty("message");
            Assert.Contains(receipt, finalMessage.GetProperty("content").GetString());
            Assert.False(finalMessage.TryGetProperty("tool_calls", out var repeated) && repeated.ValueKind == JsonValueKind.Array && repeated.GetArrayLength() > 0);
            var sent = scenario.Requests[0].Body;
            Assert.Equal("loki_get_receipt", sent.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
            Assert.False(sent.GetProperty("tools")[0].TryGetProperty("name", out _));
            Assert.False(sent.GetProperty("parallel_tool_calls").GetBoolean());
            Assert.Equal(callId, scenario.Requests[1].Body.GetProperty("messages")[2].GetProperty("tool_call_id").GetString());
            Assert.Single(request.Messages);
            AssertToolChoice(sent, false, stream);

            scenario.Evidence["toolArgumentsValidated"] = true;
            scenario.Evidence["randomToolResultConsumed"] = true;
            scenario.Evidence["stream"] = stream;
        });

    /// <summary>Responses 使用平铺工具定义和 function_call_output.call_id 回传结果。</summary>
    [Theory(Skip = "Enable live integration explicitly.", SkipType = typeof(LiveTestGate), SkipUnless = nameof(LiveTestGate.IsEnabled))]
    [InlineData(false)]
    [InlineData(true)]
    public Task ResponseTools_RoundTripRealFunctionResults(bool stream)
        => LiveScenario.RunAsync($"responses-tools-{stream}", output, async scenario =>
        {
            var parameters = ToolParameters(true, stream);
            parameters["store"] = JsonSerializer.SerializeToElement(true);
            var request = ParameterData.Responses("Use loki_get_receipt for order-42. After the tool result, answer with its receipt only.", parameters);
            var session = LiveScenario.Call();
            JsonElement response;
            List<ResponseEvent>? events = null;
            if (stream) response = ParameterData.FinalResponse(events = await scenario.ResponsesStreamAsync(request, session));
            else response = (await scenario.ResponsesAsync(request, session)).Data;
            ParameterData.Completed(response);
            var function = Assert.Single(response.GetProperty("output").EnumerateArray(), item => item.GetProperty("type").GetString() == "function_call");
            ParameterData.AssertTool(function.GetProperty("name").GetString(), function.GetProperty("arguments").GetString());
            var callId = function.GetProperty("call_id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(callId));
            if (events is not null)
            {
                var deltas = events.Where(item => item.Type == "response.function_call_arguments.delta").ToArray();
                Assert.NotEmpty(deltas);
                Assert.Equal(function.GetProperty("arguments").GetString(), string.Concat(deltas.Select(item => item.Data.GetProperty("delta").GetString())));
                Assert.Contains(events, item => item.Type == "response.function_call_arguments.done");
            }

            var receipt = "receipt-" + Guid.NewGuid().ToString("N");
            var followUp = ParameterData.Responses("unused", ToolParameters(true, false, true)) with
            {
                PreviousResponseId = response.GetProperty("id").GetString(),
                Input = new JsonArray(new JsonObject { ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = JsonSerializer.Serialize(new { receipt }) })
            };
            var result = await scenario.ResponsesAsync(followUp, session);
            ParameterData.Completed(result.Data);
            Assert.Contains(receipt, ParameterData.ResponseText(result.Data));
            var sent = scenario.Requests[0].Body;
            Assert.Equal("loki_get_receipt", sent.GetProperty("tools")[0].GetProperty("name").GetString());
            Assert.False(sent.GetProperty("tools")[0].TryGetProperty("function", out _));
            Assert.Equal(callId, scenario.Requests[1].Body.GetProperty("input")[0].GetProperty("call_id").GetString());
            Assert.Equal("function_call_output", scenario.Requests[1].Body.GetProperty("input")[0].GetProperty("type").GetString());
            AssertToolChoice(sent, true, stream);
            scenario.Evidence["toolArgumentsValidated"] = true;
            scenario.Evidence["callIdRoundTripped"] = true;
            scenario.Evidence["randomToolResultConsumed"] = true;
            scenario.Evidence["stream"] = stream;
        });

    /// <summary>保留两种协议自己的工具选择结构，流式场景测试 required。</summary>
    private static Dictionary<string, JsonElement> ToolParameters(bool responses, bool required, bool none = false)
    {
        var parameters = ParameterData.Parameters(responses);
        parameters["tools"] = ParameterData.Tool(responses);
        parameters["tool_choice"] = none ? JsonSerializer.SerializeToElement("none")
            : required ? JsonSerializer.SerializeToElement("required") : ParameterData.NamedChoice(responses);
        parameters["parallel_tool_calls"] = JsonSerializer.SerializeToElement(false);
        return parameters;
    }

    /// <summary>校验指定工具对象没有被误转换为另一协议的对象。</summary>
    private static void AssertToolChoice(JsonElement body, bool responses, bool required)
    {
        if (required) Assert.Equal("required", body.GetProperty("tool_choice").GetString());
        else Assert.Equal("loki_get_receipt", responses ? body.GetProperty("tool_choice").GetProperty("name").GetString()
            : body.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString());
    }

    /// <summary>按工具索引组合增量；不能把单个分块中的 arguments 当作完整 JSON。</summary>
    private static JsonObject AssembleChatTool(IReadOnlyList<ChatCompletionEvent> events)
    {
        var id = new StringBuilder();
        var name = new StringBuilder();
        var arguments = new StringBuilder();
        var finished = false;
        foreach (var choice in events.SelectMany(item => item.Data.GetProperty("choices").EnumerateArray()))
        {
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.GetString() == "tool_calls") finished = true;
            if (!choice.GetProperty("delta").TryGetProperty("tool_calls", out var tools)) continue;
            foreach (var tool in tools.EnumerateArray())
            {
                Assert.Equal(0, tool.GetProperty("index").GetInt32());
                if (tool.TryGetProperty("id", out var itemId) && itemId.ValueKind == JsonValueKind.String) id.Append(itemId.GetString());
                if (!tool.TryGetProperty("function", out var function)) continue;
                if (function.TryGetProperty("name", out var itemName) && itemName.ValueKind == JsonValueKind.String) name.Append(itemName.GetString());
                if (function.TryGetProperty("arguments", out var delta) && delta.ValueKind == JsonValueKind.String) arguments.Append(delta.GetString());
            }
        }
        Assert.True(finished);
        return new JsonObject
        {
            ["role"] = "assistant", ["content"] = null,
            ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = id.ToString(), ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name.ToString(), ["arguments"] = arguments.ToString() } })
        };
    }
}
