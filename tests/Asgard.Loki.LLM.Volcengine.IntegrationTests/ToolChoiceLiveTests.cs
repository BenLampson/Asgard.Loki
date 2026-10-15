using System.Text.Json;
using Xunit;

namespace Asgard.Loki.LLM.Volcengine.IntegrationTests;

/// <summary>独立保留工具禁用的严格断言，服务端忽略 none 时不能把测试改成通过。</summary>
[Trait("Suite", "Parameters")]
[Trait("Scenario", "ToolChoice")]
[Collection("Volcengine live")]
public sealed class ToolChoiceLiveTests(ITestOutputHelper output)
{
    /// <summary>先确认最终请求确实发送 none，再校验模型没有发起函数调用。</summary>
    [Theory(Skip = "Enable live integration explicitly.", SkipType = typeof(LiveTestGate), SkipUnless = nameof(LiveTestGate.IsEnabled))]
    [InlineData(false)]
    [InlineData(true)]
    public Task None_MustPreventActualToolCalls(bool responses)
        => LiveScenario.RunAsync($"tool-choice-none-{responses}", output, async scenario =>
        {
            var parameters = ParameterData.Parameters(responses);
            parameters["tools"] = ParameterData.Tool(responses);
            parameters["tool_choice"] = JsonSerializer.SerializeToElement("none");
            parameters["parallel_tool_calls"] = JsonSerializer.SerializeToElement(false);
            int toolCalls;
            if (responses)
            {
                var result = await scenario.ResponsesAsync(ParameterData.Responses("Call loki_get_receipt for order-42.", parameters));
                ParameterData.Completed(result.Data);
                toolCalls = result.Data.GetProperty("output").EnumerateArray().Count(item => item.GetProperty("type").GetString() == "function_call");
                if (result.Data.TryGetProperty("tool_choice", out var echoed))
                    scenario.Evidence["echoedToolChoice"] = echoed.ValueKind == JsonValueKind.String ? echoed.GetString()! : "object";
            }
            else
            {
                var result = await scenario.ChatAsync(ParameterData.Chat("Call loki_get_receipt for order-42.", parameters));
                var message = result.Data.GetProperty("choices")[0].GetProperty("message");
                toolCalls = message.TryGetProperty("tool_calls", out var tools) && tools.ValueKind == JsonValueKind.Array ? tools.GetArrayLength() : 0;
            }
            var sent = Assert.Single(scenario.Requests).Body;
            Assert.Equal("none", sent.GetProperty("tool_choice").GetString());
            scenario.Evidence["sentToolChoice"] = sent.GetProperty("tool_choice").GetString()!;
            scenario.Evidence["returnedToolCalls"] = toolCalls;
            scenario.Evidence["toolChoiceNoneHonored"] = toolCalls == 0;
            Assert.True(toolCalls == 0, "Upstream emitted a function call even though the final HTTP request specified tool_choice=none.");
        });
}
