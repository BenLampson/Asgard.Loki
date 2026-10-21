using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Conversion.Tests.GenerationSettingsTests;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>现代外壳通过所有相关公共 SDK 与 HTTP/SSE 入口，不覆盖其他生成设置。</summary>
public sealed class GeminiResponseFormatPipelineTests
{
    /// <summary>三个源方向与三个目标方向各覆盖完整和流式调用。</summary>
    public static TheoryData<LlmProtocol, LlmProtocol, bool> Calls
    {
        get
        {
            TheoryData<LlmProtocol, LlmProtocol, bool> result = [];
            foreach (var other in new[] { LlmProtocol.ChatCompletions, LlmProtocol.Responses, LlmProtocol.ClaudeMessages })
            foreach (var stream in new[] { false, true }) { result.Add(LlmProtocol.Gemini, other, stream); result.Add(other, LlmProtocol.Gemini, stream); }
            return result;
        }
    }

    /// <summary>核对实际出站格式、模型、推理字段与原始输入快照。</summary>
    [Theory]
    [MemberData(nameof(Calls))]
    public async Task ModernFormat_ArrivesAtFinalWire(LlmProtocol source, LlmProtocol upstream, bool stream)
    {
        var original = GeminiResponseFormatTests.ModernRequest(); var schema = original["generationConfig"]!["responseFormat"]!["text"]!["schema"]!.DeepClone();
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            var actual = upstream == LlmProtocol.Gemini ? body["generationConfig"]!["responseFormat"]!["text"]!["schema"] : Schema(body, upstream);
            Assert.True(JsonNode.DeepEquals(schema, actual)); Assert.Equal("medium", Effort(body, upstream).ToLowerInvariant());
            if (upstream == LlmProtocol.Gemini)
            {
                Assert.Equal("APPLICATION_JSON", body["generationConfig"]!["responseFormat"]!["text"]!["mimeType"]!.GetValue<string>());
                Assert.False(body["generationConfig"]!.AsObject().ContainsKey("responseJsonSchema"));
            }
            if (!stream) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ConversionFixtures.Response(upstream).ToJsonString(), Encoding.UTF8, "application/json") };
            var events = string.Concat(ConversionFixtures.Stream(upstream).Select(item => "data: " + item.ToJsonString() + "\n\n"))
                + (upstream == LlmProtocol.ChatCompletions ? "data: [DONE]\n\n" : "");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
        });
        await using var client = ConvertingProviderTests.Build(handler, source, upstream, options: new()
        { GeminiOutputFormat = GeminiOutputFormat.ResponseFormat, DefaultSchemaName = "result", DefaultMaxTokens = 4096, AllowLoss = true });
        var input = source == LlmProtocol.Gemini ? original : Request(source); input["model"] = "public";
        if (source != LlmProtocol.Gemini) SetFormat(input, source, schema);
        if (source == LlmProtocol.Gemini) input["generationConfig"]!["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = "MEDIUM" };
        else if (source == LlmProtocol.ClaudeMessages) input["output_config"]!["effort"] = "medium";
        else SetEffort(input, source, "medium");
        var before = input.ToJsonString();
        if (stream) Assert.NotEmpty(await ConvertingProviderTests.StreamAsync(client, source, input));
        else Assert.NotNull((await ConvertingProviderTests.CompleteAsync(client, source, input)).Metadata.Conversion);
        Assert.Equal(before, input.ToJsonString()); Assert.Equal(1, handler.Calls);
    }
}
