using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Conversion.Tests.GenerationSettingsTests;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>生成设置经过公共 SDK、实际转换供应商和原生 HTTP 适配器后仍落在正确字段。</summary>
public sealed class SettingsPipelineTests
{
    /// <summary>十二个方向各自覆盖完整调用与流式调用。</summary>
    public static TheoryData<LlmProtocol, LlmProtocol, bool> Calls => ConvertingProviderTests.Calls;

    /// <summary>验证最终网络报文，避免纯转换器测试通过但 SDK 路径丢失配置。</summary>
    [Theory]
    [MemberData(nameof(Calls))]
    public async Task Settings_ArriveAtNativeWire(LlmProtocol source, LlmProtocol upstream, bool stream)
    {
        var schema = JsonNode.Parse("""{"type":"object","properties":{"x":{"type":"integer","minimum":0}},"additionalProperties":false}""")!;
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            Assert.Equal("medium", Effort(body, upstream).ToLowerInvariant());
            Assert.True(JsonNode.DeepEquals(schema, Schema(body, upstream)));
            if (upstream != LlmProtocol.Gemini) Assert.Equal("model-a", body["model"]!.GetValue<string>());
            if (!stream) return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(ConversionFixtures.Response(upstream).ToJsonString(), Encoding.UTF8, "application/json") };
            var data = string.Concat(ConversionFixtures.Stream(upstream).Select(e => "data: " + e.ToJsonString() + "\n\n"))
                + (upstream == LlmProtocol.ChatCompletions ? "data: [DONE]\n\n" : "");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") };
        });
        await using var client = ConvertingProviderTests.Build(handler, source, upstream, options:
            new() { AllowLoss = true, DefaultSchemaName = "result", DefaultMaxTokens = 4096 });
        var input = Request(source); input["model"] = "public";
        SetFormat(input, source, schema);
        var effort = new JsonObject(); SetEffort(effort, source, "medium");
        foreach (var field in effort)
        {
            if (field.Value is JsonObject fields && input[field.Key] is JsonObject existing)
                foreach (var nested in fields) existing[nested.Key] = nested.Value?.DeepClone();
            else input[field.Key] = field.Value?.DeepClone();
        }
        var before = input.ToJsonString();
        if (stream) Assert.NotEmpty(await ConvertingProviderTests.StreamAsync(client, source, input));
        else Assert.NotNull((await ConvertingProviderTests.CompleteAsync(client, source, input)).Metadata.Conversion);
        Assert.Equal(1, handler.Calls); Assert.Equal(before, input.ToJsonString());
    }
}
