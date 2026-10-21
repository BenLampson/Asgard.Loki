using System.Text.Json.Nodes;
using Xunit;
using static Asgard.Loki.LLM.Conversion.Tests.GenerationSettingsTests;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>现行 Gemini responseFormat.text 的原生外壳与严格字段保真。</summary>
public sealed class GeminiResponseFormatTests
{
    /// <summary>现行 JSON Schema 外壳读取后，不改写嵌套约束、引用、零值或布尔 Schema。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)]
    public void ModernSchema_MapsToOtherProtocolsWithoutConstraintLoss(LlmProtocol target)
    {
        var source = ModernRequest(); var original = source.ToJsonString();
        var schema = source["generationConfig"]!["responseFormat"]!["text"]!["schema"]!;
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, target, new() { DefaultSchemaName = "result", DefaultMaxTokens = 4096 });
        Assert.Empty(result.Diagnostics); Assert.True(JsonNode.DeepEquals(schema, Schema(JsonNode.Parse(result.Data.GetRawText())!.AsObject(), target)));
        Assert.Equal(original, source.ToJsonString()); Assert.True(JsonNode.DeepEquals(source, JsonNode.Parse(result.OriginalData.GetRawText())));
    }

    /// <summary>未知配置不能被当成新协议字段而静默忽略，AllowLoss 也必须保留精确路径诊断。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ModernUnknownFields_HavePreciseDiagnostics(bool nested)
    {
        var source = ModernRequest(); var format = source["generationConfig"]!["responseFormat"]!.AsObject();
        (nested ? format["text"]!.AsObject() : format)["future"] = null;
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, LlmProtocol.Responses, new() { DefaultSchemaName = "result" }));
        var path = "$.generationConfig.responseFormat" + (nested ? ".text" : "") + ".future";
        Assert.Contains(error.Diagnostics, item => item.Path == path);
        var allowed = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, LlmProtocol.Responses, new() { DefaultSchemaName = "result", AllowLoss = true });
        Assert.Contains(allowed.Diagnostics, item => item.Path == path);
    }

    /// <summary>不明确选择现代与旧外壳的混合文本配置不能按先后顺序擅自选一种。</summary>
    [Theory]
    [InlineData("responseMimeType", "\"application/json\"")]
    [InlineData("responseJsonSchema", "{\"type\":\"object\"}")]
    [InlineData("responseSchema", "{\"type\":\"OBJECT\"}")]
    public void ConflictingTextFormats_AreRejectedEvenWithAllowLoss(string key, string value)
    {
        var source = ModernRequest(); source["generationConfig"]![key] = JsonNode.Parse(value);
        Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, LlmProtocol.Responses,
            new() { DefaultSchemaName = "result", AllowLoss = true }));
    }

    /// <summary>同协议复制继续保持原生字段，不受跨协议配置逻辑影响。</summary>
    [Fact]
    public void SameProtocol_KeepsModernAndFutureFields()
    {
        var source = ModernRequest(); source["generationConfig"]!["responseFormat"]!["image"] = new JsonObject { ["future"] = false };
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, LlmProtocol.Gemini);
        Assert.True(JsonNode.DeepEquals(source, JsonNode.Parse(result.Data.GetRawText()))); Assert.Empty(result.Diagnostics);
    }

    /// <summary>独立原生请求样本，Schema 内所有语义都应保留。</summary>
    internal static JsonObject ModernRequest()
    {
        var source = Request(LlmProtocol.Gemini);
        source["generationConfig"] = JsonNode.Parse("""{"responseFormat":{"text":{"mimeType":"APPLICATION_JSON","schema":{"type":"object","properties":{"n":{"type":["integer","null"],"minimum":0},"items":{"type":"array","items":{"$ref":"#/$defs/item"},"minItems":0}},"required":["n"],"additionalProperties":false,"$defs":{"item":{"anyOf":[{"type":"string"},{"type":"boolean"}]}},"x-future":{"keep":false}}}}}""");
        return source;
    }
}
