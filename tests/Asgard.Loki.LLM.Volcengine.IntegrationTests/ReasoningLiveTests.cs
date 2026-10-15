using System.Text.Json;
using Xunit;

namespace Asgard.Loki.LLM.Volcengine.IntegrationTests;

/// <summary>通过真实推理 token 数、流式字段和最终答案验证开关，不推断不同档位的确定耗时。</summary>
[Trait("Suite", "Parameters")]
[Trait("Scenario", "Reasoning")]
[Collection("Volcengine live")]
public sealed class ReasoningLiveTests(ITestOutputHelper output)
{
    /// <summary>两种协议、两种传输模式分别检查显式启用与禁用推理。</summary>
    [Theory(Skip = "Enable live integration explicitly.", SkipType = typeof(LiveTestGate), SkipUnless = nameof(LiveTestGate.IsEnabled))]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public Task Thinking_ChangesActualReasoningUsage(bool responses, bool stream, bool enabled)
        => LiveScenario.RunAsync($"reasoning-{responses}-{stream}-{enabled}", output, async scenario =>
        {
            const string prompt = "Compute (17*23)+(19*29)+(31*37). Reply with the final integer only.";
            var parameters = ParameterData.Parameters(responses, enabled);
            if (enabled)
            {
                if (responses) parameters["reasoning"] = JsonSerializer.SerializeToElement(new { effort = "low" });
                else parameters["reasoning_effort"] = JsonSerializer.SerializeToElement("low");
            }
            long tokens;
            string text;
            var reasoningEvents = 0;
            if (responses)
            {
                var request = ParameterData.Responses(prompt, parameters);
                JsonElement response;
                if (stream)
                {
                    var events = await scenario.ResponsesStreamAsync(request);
                    response = ParameterData.FinalResponse(events);
                    reasoningEvents = events.Count(item => item.Type?.Contains("reasoning", StringComparison.Ordinal) == true);
                }
                else response = (await scenario.ResponsesAsync(request)).Data;
                ParameterData.Completed(response);
                tokens = response.GetProperty("usage").GetProperty("output_tokens_details").GetProperty("reasoning_tokens").GetInt64();
                text = ParameterData.ResponseText(response);
                if (enabled) Assert.Contains(response.GetProperty("output").EnumerateArray(), item => item.GetProperty("type").GetString() == "reasoning");
                if (enabled && stream) Assert.True(reasoningEvents > 0, "Reasoning stream events were not preserved.");
            }
            else
            {
                if (stream) parameters["stream_options"] = JsonSerializer.SerializeToElement(new { include_usage = true });
                var request = ParameterData.Chat(prompt, parameters);
                if (stream)
                {
                    var events = await scenario.ChatStreamAsync(request);
                    var usage = events.Last(item => item.Data.TryGetProperty("usage", out var value) && value.ValueKind == JsonValueKind.Object).Data.GetProperty("usage");
                    tokens = usage.GetProperty("completion_tokens_details").GetProperty("reasoning_tokens").GetInt64();
                    var choices = events.SelectMany(item => item.Data.GetProperty("choices").EnumerateArray()).ToArray();
                    Assert.Contains(choices, item => item.TryGetProperty("finish_reason", out var finish) && finish.GetString() == "stop");
                    text = string.Concat(choices.Where(item => item.GetProperty("delta").TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.String).Select(item => item.GetProperty("delta").GetProperty("content").GetString()));
                    reasoningEvents = choices.Count(item => item.GetProperty("delta").TryGetProperty("reasoning_content", out var reason)
                        && reason.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(reason.GetString()));
                    if (enabled) Assert.True(reasoningEvents > 0, "Reasoning delta fields were not preserved.");
                }
                else
                {
                    var result = await scenario.ChatAsync(request);
                    var choice = result.Data.GetProperty("choices")[0];
                    Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
                    tokens = result.Data.GetProperty("usage").GetProperty("completion_tokens_details").GetProperty("reasoning_tokens").GetInt64();
                    text = choice.GetProperty("message").GetProperty("content").GetString()!;
                    if (enabled) Assert.False(string.IsNullOrWhiteSpace(choice.GetProperty("message").GetProperty("reasoning_content").GetString()));
                }
            }

            Assert.False(string.IsNullOrWhiteSpace(text));
            var plainInteger = long.TryParse(text.Trim(), out var answer);
            if (enabled) Assert.True(tokens > 0, "Upstream did not report actual reasoning tokens.");
            else Assert.Equal(0, tokens);
            var body = Assert.Single(scenario.Requests).Body;
            Assert.Equal(enabled ? "enabled" : "disabled", body.GetProperty("thinking").GetProperty("type").GetString());
            if (enabled) Assert.Equal("low", responses ? body.GetProperty("reasoning").GetProperty("effort").GetString() : body.GetProperty("reasoning_effort").GetString());
            Assert.False(body.TryGetProperty(responses ? "reasoning_effort" : "reasoning", out _));
            scenario.Evidence["reasoningEnabled"] = enabled;
            scenario.Evidence["reasoningTokens"] = tokens;
            scenario.Evidence["reasoningStreamEvents"] = reasoningEvents;
            // 推理开关以真实 token 用量判定；合成算式的准确率另记，不冒充协议保证。
            scenario.Evidence["plainIntegerAnswer"] = plainInteger;
            scenario.Evidence["arithmeticAnswer"] = plainInteger ? answer : "not-a-plain-integer";
            scenario.Evidence["arithmeticAnswerCorrect"] = plainInteger && answer == 2089;
        });
}
