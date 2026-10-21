using System.Text.Json;
using Xunit;

namespace Asgard.Loki.LLM.Volcengine.IntegrationTests;

/// <summary>分别验证两种协议普通与流式的严格 Schema 实际生效。</summary>
[Trait("Suite", "Parameters")]
[Trait("Scenario", "Structured")]
[Collection("Volcengine live")]
public sealed class StructuredOutputLiveTests(ITestOutputHelper output)
{
    /// <summary>流式输出也必须在完整枚举后满足同一份 Schema。</summary>
    [Theory(Skip = "Enable live integration explicitly.", SkipType = typeof(LiveTestGate), SkipUnless = nameof(LiveTestGate.IsEnabled))]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task StrictSchema_ConstrainsActualOutput(bool responses, bool stream)
        => LiveScenario.RunAsync($"schema-{responses}-{stream}", output, async scenario =>
        {
            const string prompt = "Create an object under the supplied schema. Use count 3, active false, tags alpha and beta, and note null.";
            var parameters = ParameterData.Parameters(responses);
            if (responses)
                parameters["text"] = JsonSerializer.SerializeToElement(new
                { format = new { type = "json_schema", name = "loki_result", strict = true, schema = ParameterData.Schema() } });
            else
                parameters["response_format"] = JsonSerializer.SerializeToElement(new
                { type = "json_schema", json_schema = new { name = "loki_result", strict = true, schema = ParameterData.Schema() } });

            string text;
            if (responses)
            {
                var request = ParameterData.Responses(prompt, parameters);
                var final = stream ? ParameterData.FinalResponse(await scenario.ResponsesStreamAsync(request))
                    : (await scenario.ResponsesAsync(request)).Data;
                ParameterData.Completed(final);
                text = ParameterData.ResponseText(final);
                var format = Assert.Single(scenario.Requests).Body.GetProperty("text").GetProperty("format");
                Assert.True(format.GetProperty("strict").GetBoolean());
                Assert.False(format.TryGetProperty("json_schema", out _));
            }
            else
            {
                var request = ParameterData.Chat(prompt, parameters);
                if (stream)
                {
                    var events = await scenario.ChatStreamAsync(request);
                    var choices = events.SelectMany(item => item.Data.GetProperty("choices").EnumerateArray()).ToArray();
                    Assert.Contains(choices, item => item.TryGetProperty("finish_reason", out var finish) && finish.GetString() == "stop");
                    text = string.Concat(choices.Where(item => item.GetProperty("delta").TryGetProperty("content", out var value)
                        && value.ValueKind == JsonValueKind.String).Select(item => item.GetProperty("delta").GetProperty("content").GetString()));
                }
                else
                {
                    var result = await scenario.ChatAsync(request);
                    Assert.Equal("stop", result.Data.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
                    text = result.Data.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
                }
                Assert.True(Assert.Single(scenario.Requests).Body.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
            }

            ParameterData.AssertSchema(text);
            var sent = Assert.Single(scenario.Requests).Body;
            Assert.Equal(stream, sent.GetProperty("stream").GetBoolean());
            Assert.False(sent.TryGetProperty(responses ? "response_format" : "text", out _));
            scenario.Evidence["schemaValidated"] = true;
            scenario.Evidence["stream"] = stream;
            scenario.Evidence["validatedFields"] = new[] { "kind", "count", "active", "tags", "note", "metadata.source" };
        });
}
