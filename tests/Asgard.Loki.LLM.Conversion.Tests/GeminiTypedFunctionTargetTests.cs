using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>函数目标方言独立显式选择，工具定义与调用/回填身份均通过完整快照验证。</summary>
public sealed class GeminiTypedFunctionTargetTests
{
    /// <summary>复用独立手工原生样本反方向验收，不用转换器生成预期 JSON。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)]
    public void FullRequest_MatchesIndependentTypedFunctionSnapshot(LlmProtocol sourceProtocol)
    {
        var fixture = GeminiTypedFunctionTests.Fixture(); var source = fixture[sourceProtocol.ToString()]!.AsObject(); var before = source.ToJsonString();
        var result = LlmProtocolConverter.ConvertRequest(source, sourceProtocol, LlmProtocol.Gemini, Options());
        Assert.Empty(result.Diagnostics); Assert.Equal(before, source.ToJsonString());
        Assert.True(JsonNode.DeepEquals(fixture["Gemini"], JsonNode.Parse(result.Data.GetRawText())), result.Data.GetRawText());
        var replay = LlmProtocolConverter.ConvertRequest(JsonNode.Parse(result.Data.GetRawText())!.AsObject(), LlmProtocol.Gemini, sourceProtocol);
        Assert.True(JsonNode.DeepEquals(source, JsonNode.Parse(replay.Data.GetRawText())), replay.Data.GetRawText());
    }

    /// <summary>函数参数与文本格式的两个选择不能互相覆盖，也不能给函数添加文本顺序承诺。</summary>
    [Theory]
    [InlineData(false, GeminiOutputFormat.LegacyJsonSchema)] [InlineData(true, GeminiOutputFormat.LegacyJsonSchema)]
    [InlineData(false, GeminiOutputFormat.ResponseFormat)] [InlineData(true, GeminiOutputFormat.ResponseFormat)]
    [InlineData(false, GeminiOutputFormat.LegacySchema)] [InlineData(true, GeminiOutputFormat.LegacySchema)]
    public void FunctionAndOutputFormats_AreIndependent(bool typedFunctions, GeminiOutputFormat outputFormat)
    {
        var source = GeminiTypedFunctionTests.Fixture()["ClaudeMessages"]!.AsObject();
        source["output_config"] = JsonNode.Parse("""{"format":{"type":"json_schema","schema":{"type":"object","properties":{"ok":{"type":"boolean"}}}}}""");
        var options = new LlmConversionOptions
        {
            GeminiOutputFormat = outputFormat,
            GeminiFunctionSchemaFormat = typedFunctions ? GeminiFunctionSchemaFormat.LegacySchema : GeminiFunctionSchemaFormat.JsonSchema
        };
        var target = JsonNode.Parse(LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, options).Data.GetRawText())!;
        var config = target["generationConfig"]!.AsObject();
        if (outputFormat == GeminiOutputFormat.ResponseFormat)
            Assert.Equal("APPLICATION_JSON", config["responseFormat"]!["text"]!["mimeType"]!.GetValue<string>());
        else
        {
            Assert.Equal("application/json", config["responseMimeType"]!.GetValue<string>());
            Assert.Equal(outputFormat == GeminiOutputFormat.LegacySchema, config.ContainsKey("responseSchema"));
            Assert.Equal(outputFormat == GeminiOutputFormat.LegacyJsonSchema, config.ContainsKey("responseJsonSchema"));
        }
        var function = target["tools"]![0]!["functionDeclarations"]![0]!.AsObject();
        Assert.Equal(typedFunctions, function.ContainsKey("parameters")); Assert.Equal(!typedFunctions, function.ContainsKey("parametersJsonSchema"));
        Assert.False((function[typedFunctions ? "parameters" : "parametersJsonSchema"] as JsonObject)!.ContainsKey("propertyOrdering"));
    }

    /// <summary>typed 不能承载的参数约束必须硬失败，即使允许普通损耗。</summary>
    [Theory]
    [InlineData("additionalProperties", "false")] [InlineData("$ref", "\"#/$defs/n\"")]
    [InlineData("futureConstraint", "null")]
    public void InvalidTypedConstraint_IsNeverDropped(string key, string encoded)
    {
        var source = GeminiTypedFunctionTests.Fixture()["ClaudeMessages"]!.AsObject();
        GeminiTypedFunctionTests.Parameters(source, LlmProtocol.ClaudeMessages)[key] = JsonNode.Parse(encoded);
        var failure = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, Options() with { AllowLoss = true }));
        Assert.Equal("schema_keyword_not_representable", Assert.Single(failure.Diagnostics).Code);
        Assert.Equal("$.tools[].function.parameters." + key, failure.Diagnostics[0].Path);
        var original = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini);
        Assert.True(JsonNode.DeepEquals(GeminiTypedFunctionTests.Parameters(source, LlmProtocol.ClaudeMessages),
            JsonNode.Parse(original.Data.GetRawText())!["tools"]![0]!["functionDeclarations"]![0]!["parametersJsonSchema"]));
    }

    /// <summary>无效选择在任何协议的调用前拒绝，而非默认为另一种格式。</summary>
    [Fact]
    public void UnknownFormat_IsRejected()
    {
        var source = GeminiTypedFunctionTests.Source();
        Assert.Throws<ArgumentOutOfRangeException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, LlmProtocol.Gemini,
            new() { GeminiFunctionSchemaFormat = (GeminiFunctionSchemaFormat)999 }));
    }

    /// <summary>同协议复制不因目标方言选项而改写原生 JSON Schema 函数定义。</summary>
    [Fact]
    public void NativeCopy_DoesNotMigrateFunctionSchema()
    {
        var source = GeminiTypedFunctionTests.Source(); var function = GeminiTypedFunctionTests.Function(source);
        function.Remove("parameters"); function["parametersJsonSchema"] = JsonNode.Parse("""{"type":"object","additionalProperties":false}""");
        var copy = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, LlmProtocol.Gemini, Options());
        Assert.Equal(source.ToJsonString(), JsonNode.Parse(copy.Data.GetRawText())!.ToJsonString());
    }

    /// <summary>同一请求中的 strict 文本输出顺序不能泄漏到普通函数参数。</summary>
    [Fact]
    public void TextOrdering_DoesNotLeakIntoFunctionParameters()
    {
        var source = GeminiTypedFunctionTests.Fixture()["Responses"]!.AsObject();
        GenerationSettingsTests.SetFormat(source, LlmProtocol.Responses, JsonNode.Parse("""{"type":"object","properties":{"ok":{"type":"boolean"}}}""")!);
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, LlmProtocol.Gemini,
            Options() with { GeminiOutputFormat = GeminiOutputFormat.LegacySchema, AllowLoss = true });
        var target = JsonNode.Parse(result.Data.GetRawText())!;
        Assert.True(target["generationConfig"]!["responseSchema"]!.AsObject().ContainsKey("propertyOrdering"));
        Assert.False(target["tools"]![0]!["functionDeclarations"]![0]!["parameters"]!.AsObject().ContainsKey("propertyOrdering"));
    }

    /// <summary>目标默认文本格式保持不变，仅显式改变函数参数方言。</summary>
    internal static LlmConversionOptions Options() => new() { GeminiFunctionSchemaFormat = GeminiFunctionSchemaFormat.LegacySchema };
}
