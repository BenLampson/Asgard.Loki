using System.Text.Json.Nodes;
using Xunit;
using static Asgard.Loki.LLM.Conversion.Tests.GenerationSettingsTests;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>显式目标选择、JSON/plain 模式及现代外壳的非法组合。</summary>
public sealed class GeminiOutputModeTests
{
    /// <summary>默认仍用旧外壳；现代字段只由明确的目标选项启用，Schema 本身完全不变。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TargetSchemaEnvelope_IsExplicit(bool modern)
    {
        var source = Request(LlmProtocol.ClaudeMessages); var schema = JsonNode.Parse("""{"type":"object","properties":{"ok":{"type":"boolean"}},"additionalProperties":false}""")!;
        SetFormat(source, LlmProtocol.ClaudeMessages, schema);
        var result = Convert(source, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, new()
        { GeminiOutputFormat = modern ? GeminiOutputFormat.ResponseFormat : GeminiOutputFormat.LegacyJsonSchema });
        var config = result["generationConfig"]!.AsObject();
        Assert.Equal(modern, config.ContainsKey("responseFormat")); Assert.Equal(!modern, config.ContainsKey("responseMimeType")); Assert.Equal(!modern, config.ContainsKey("responseJsonSchema"));
        var actual = modern ? config["responseFormat"]!["text"]!["schema"] : config["responseJsonSchema"];
        Assert.True(JsonNode.DeepEquals(schema, actual));
    }

    /// <summary>不带 Schema 的 JSON 模式与纯文本模式保持不同，且不被伪造成 JSON Schema。</summary>
    [Theory]
    [InlineData("json_object", "APPLICATION_JSON")]
    [InlineData("text", "TEXT_PLAIN")]
    public void PlainModes_MapInBothDirections(string kind, string mime)
    {
        var source = Request(LlmProtocol.ChatCompletions); source["response_format"] = new JsonObject { ["type"] = kind };
        var gemini = Convert(source, LlmProtocol.ChatCompletions, LlmProtocol.Gemini, new() { GeminiOutputFormat = GeminiOutputFormat.ResponseFormat });
        Assert.Equal(mime, gemini["generationConfig"]!["responseFormat"]!["text"]!["mimeType"]!.GetValue<string>());
        Assert.False(gemini["generationConfig"]!["responseFormat"]!["text"]!.AsObject().ContainsKey("schema"));
        var returned = Convert(gemini, LlmProtocol.Gemini, LlmProtocol.ChatCompletions);
        Assert.Equal(kind, returned["response_format"]!["type"]!.GetValue<string>());
    }

    /// <summary>Schema 没有 JSON MIME、显式 null 或错误结构，均不可降级为无约束模式。</summary>
    [Theory]
    [InlineData("""{"text":{"schema":{}}}""")]
    [InlineData("""{"text":{"mimeType":"TEXT_PLAIN","schema":{}}}""")]
    [InlineData("""{"text":{"mimeType":"APPLICATION_JSON","schema":null}}""")]
    [InlineData("""{"text":null}""")]
    [InlineData("null")]
    public void InvalidModernEnvelope_IsRejected(string json)
    {
        var source = Request(LlmProtocol.Gemini); source["generationConfig"] = new JsonObject { ["responseFormat"] = JsonNode.Parse(json) };
        Assert.Throws<ArgumentException>(() => Convert(source, LlmProtocol.Gemini, LlmProtocol.Responses, new() { AllowLoss = true, DefaultSchemaName = "result" }));
    }

    /// <summary>新外壳中不支持的 MIME 与其他模态仍严格诊断，不假装是可转换的文本。</summary>
    [Theory]
    [InlineData("""{"text":{"mimeType":"FUTURE_MIME"}}""", "$.generationConfig.responseFormat.text.mimeType")]
    [InlineData("""{"audio":{"mimeType":"AUDIO_WAV"}}""", "$.generationConfig.responseFormat.audio")]
    [InlineData("""{"image":{"mimeType":"IMAGE_JPEG"}}""", "$.generationConfig.responseFormat.image")]
    public void UnsupportedModalities_KeepDiagnostics(string json, string path)
    {
        var source = Request(LlmProtocol.Gemini); source["generationConfig"] = new JsonObject { ["responseFormat"] = JsonNode.Parse(json) };
        var error = Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.Gemini, LlmProtocol.Responses));
        Assert.Contains(error.Diagnostics, item => item.Path == path);
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, LlmProtocol.Responses, new() { AllowLoss = true });
        Assert.Contains(result.Diagnostics, item => item.Path == path); Assert.False(result.Data.TryGetProperty("text", out _));
    }

    /// <summary>没有文本格式的空对象不应产生新的输出要求。</summary>
    [Theory]
    [InlineData("{}")] [InlineData("{\"text\":{}}")]
    public void UnspecifiedText_DoesNotInventFormat(string json)
    {
        var source = Request(LlmProtocol.Gemini); source["generationConfig"] = new JsonObject { ["responseFormat"] = JsonNode.Parse(json) };
        Assert.False(Convert(source, LlmProtocol.Gemini, LlmProtocol.Responses).ContainsKey("text"));
    }

    /// <summary>尚未实现的配置枚举值不能静默退回默认外壳。</summary>
    [Fact]
    public void UnknownTargetEnvelope_IsRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Convert(Request(LlmProtocol.ClaudeMessages), LlmProtocol.ClaudeMessages, LlmProtocol.Gemini,
            new() { GeminiOutputFormat = (GeminiOutputFormat)99 }));
}
