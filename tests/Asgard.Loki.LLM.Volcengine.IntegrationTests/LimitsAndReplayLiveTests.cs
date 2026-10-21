using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Volcengine.IntegrationTests;

/// <summary>验证输出长度真正限制生成，并验证不透明推理项可以无损回传。</summary>
[Trait("Suite", "Parameters")]
[Collection("Volcengine live")]
public sealed class LimitsAndReplayLiveTests(ITestOutputHelper output)
{
    /// <summary>不能仅断言参数发出，还必须观察 length 或 incomplete 终态。</summary>
    [Theory(Skip = "Enable live integration explicitly.", SkipType = typeof(LiveTestGate), SkipUnless = nameof(LiveTestGate.IsEnabled))]
    [Trait("Scenario", "Limits")]
    [InlineData(false)]
    [InlineData(true)]
    public Task OutputLimit_ChangesActualTermination(bool responses)
        => LiveScenario.RunAsync($"limit-{responses}", output, async scenario =>
        {
            const string prompt = "Write a detailed 300-word explanation of sorting algorithms.";
            var parameters = ParameterData.Parameters(responses, limit: 1);
            if (responses)
            {
                var result = await scenario.ResponsesAsync(ParameterData.Responses(prompt, parameters));
                Assert.Equal("incomplete", result.Data.GetProperty("status").GetString());
                Assert.Equal("length", result.Data.GetProperty("incomplete_details").GetProperty("reason").GetString());
                scenario.Evidence["nativeIncompleteReason"] = result.Data.GetProperty("incomplete_details").GetProperty("reason").GetString()!;
                Assert.InRange(result.Data.GetProperty("usage").GetProperty("output_tokens").GetInt32(), 0, 1);
            }
            else
            {
                var result = await scenario.ChatAsync(ParameterData.Chat(prompt, parameters));
                Assert.Equal("length", result.Data.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
                Assert.InRange(result.Data.GetProperty("usage").GetProperty("completion_tokens").GetInt32(), 0, 1);
            }
            scenario.Evidence["outputTokenLimit"] = 1;
            scenario.Evidence["limitTerminationObserved"] = true;
        });

    /// <summary>完整回传原始 output，其中的 encrypted_content 不解密、不重写、不落盘。</summary>
    [Fact(Skip = "Enable live integration explicitly.", SkipType = typeof(LiveTestGate), SkipUnless = nameof(LiveTestGate.IsEnabled))]
    [Trait("Scenario", "Replay")]
    public Task Responses_ResubmitsOpaqueReasoningWithoutMutation()
        => LiveScenario.RunAsync("reasoning-replay", output, async scenario =>
        {
            var parameters = ParameterData.Parameters(true, true);
            parameters["reasoning"] = JsonSerializer.SerializeToElement(new { effort = "low" });
            var request = ParameterData.Responses("Compute (17*23)+(19*29)+(31*37). Reply with the final integer only.", parameters);
            var original = await scenario.ResponsesAsync(request);
            ParameterData.Completed(original.Data);
            var reasoning = Assert.Single(original.Data.GetProperty("output").EnumerateArray(), item => item.GetProperty("type").GetString() == "reasoning");
            Assert.False(string.IsNullOrWhiteSpace(reasoning.GetProperty("encrypted_content").GetString()));
            var input = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = request.Input!.DeepClone() });
            foreach (var item in original.Data.GetProperty("output").EnumerateArray()) input.Add(JsonNode.Parse(item.GetRawText()));
            input.Add(new JsonObject { ["role"] = "user", ["content"] = "Add one to your preceding final answer. Reply with the integer only." });
            var followUp = ParameterData.Responses("unused", ParameterData.Parameters(true)) with { Input = input };
            var result = await scenario.ResponsesAsync(followUp);
            ParameterData.Completed(result.Data);
            Assert.Equal("2090", ParameterData.ResponseText(result.Data).Trim());
            var returned = scenario.Requests[1].Body.GetProperty("input").EnumerateArray()
                .Single(item => item.TryGetProperty("type", out var type) && type.GetString() == "reasoning");
            Assert.True(JsonElement.DeepEquals(reasoning, returned), "Opaque reasoning fields changed during request serialization.");
            Assert.False(scenario.Requests[1].Body.TryGetProperty("previous_response_id", out _));
            scenario.Evidence["encryptedContentPreserved"] = true;
            scenario.Evidence["statelessReplayAccepted"] = true;
        });
}
