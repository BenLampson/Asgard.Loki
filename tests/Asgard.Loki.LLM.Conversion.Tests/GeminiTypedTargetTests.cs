using System.Text.Json.Nodes;
using Xunit;
using static Asgard.Loki.LLM.Conversion.Tests.GenerationSettingsTests;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>显式 typed 目标通过独立快照验证，不能靠删除校验约束取得成功。</summary>
public sealed class GeminiTypedTargetTests
{
    /// <summary>三种原生源的完整目标报文独立手工指定，输出顺序承诺按源协议处理。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)]
    public void CompleteRequest_MatchesIndependentTypedSnapshot(LlmProtocol sourceProtocol)
    {
        var fixture = Fixture(); var source = fixture[sourceProtocol.ToString()]!.AsObject(); var before = source.ToJsonString();
        var result = LlmProtocolConverter.ConvertRequest(source, sourceProtocol, LlmProtocol.Gemini, TypedOptions(sourceProtocol != LlmProtocol.ClaudeMessages));
        Assert.True(JsonNode.DeepEquals(Expected(fixture, sourceProtocol), JsonNode.Parse(result.Data.GetRawText())), result.Data.GetRawText());
        if (sourceProtocol == LlmProtocol.ClaudeMessages) Assert.Empty(result.Diagnostics);
        else Assert.Equal("format_metadata_not_representable", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(before, source.ToJsonString());
        var replay = LlmProtocolConverter.ConvertRequest(JsonNode.Parse(result.Data.GetRawText())!.AsObject(), LlmProtocol.Gemini, sourceProtocol,
            new() { DefaultSchemaName = "result" });
        Assert.True(JsonNode.DeepEquals(Schema(source, sourceProtocol), Schema(JsonNode.Parse(replay.Data.GetRawText())!.AsObject(), sourceProtocol)));
    }

    /// <summary>不支持的 JSON Schema 校验必须在允许损耗时仍硬失败，保留精确字段路径。</summary>
    [Theory]
    [InlineData("$ref", "\"#/$defs/n\"")] [InlineData("additionalProperties", "false")]
    [InlineData("const", "1")] [InlineData("allOf", "[]")] [InlineData("not", "{}")]
    [InlineData("futureConstraint", "null")] [InlineData("exclusiveMinimum", "0")]
    public void UnsupportedConstraints_AreNeverDeleted(string key, string json)
    {
        var source = Source("""{"type":"object"}"""); Schema(source, LlmProtocol.ClaudeMessages)[key] = JsonNode.Parse(json);
        var failure = Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, TypedOptions(true)));
        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal("schema_keyword_not_representable", diagnostic.Code); Assert.Equal("$format.schema." + key, diagnostic.Path);
    }

    /// <summary>集合枚举与多类型联合不得只保留第一项；空枚举不能变成不受限制的值。</summary>
    [Theory]
    [InlineData("""{"type":["integer","string"]}""", "schema_union_not_representable")]
    [InlineData("""{"type":"string","enum":[]}""", "schema_enum_not_representable")]
    [InlineData("""{"type":["string","null"],"enum":["yes",null]}""", "schema_enum_not_representable")]
    [InlineData("""{"type":"integer","enum":[1,2]}""", "schema_enum_not_representable")]
    public void NonrepresentableTypesAndEnums_AreRejected(string schema, string code)
    {
        var failure = Assert.Throws<LlmConversionException>(() => Convert(Source(schema), LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, TypedOptions(true)));
        Assert.Equal(code, Assert.Single(failure.Diagnostics).Code);
    }

    /// <summary>double 最短十进制报文改变约束值时不能钳制或舍入。</summary>
    [Theory]
    [InlineData("9223372036854775807")] [InlineData("1.234567890123456789")]
    [InlineData("0.10000000000000001")] [InlineData("1e-999")] [InlineData("1e999")]
    public void RoundedDoubleBounds_AreRejected(string number)
    {
        var failure = Assert.Throws<LlmConversionException>(() => Convert(Source("{\"type\":\"number\",\"maximum\":" + number + "}"),
            LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, TypedOptions(true)));
        Assert.Equal("schema_number_not_representable", Assert.Single(failure.Diagnostics).Code);
    }

    /// <summary>等价十进制指数写法、负零与常见小数按原始 JSON 报文保留。</summary>
    [Theory]
    [InlineData("0.1")] [InlineData("1.00e2")] [InlineData("-0")]
    [InlineData("1.2345678901234567")] [InlineData("1e100")]
    public void RepresentableBounds_KeepOriginalDigits(string number)
    {
        var target = Convert(Source("{\"type\":\"number\",\"minimum\":" + number + "}"), LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, TypedOptions());
        Assert.Equal(number, target["generationConfig"]!["responseSchema"]!["minimum"]!.ToJsonString());
    }

    /// <summary>int64 计数转换为原生字符串，最大值不经 double 舍入。</summary>
    [Fact]
    public void IntegerLimits_KeepInt64Precision()
    {
        var target = Convert(Source("""{"type":"array","maxItems":9223372036854775807}"""), LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, TypedOptions());
        Assert.Equal("9223372036854775807", target["generationConfig"]!["responseSchema"]!["maxItems"]!.GetValue<string>());
    }

    /// <summary>类型加 null 的联合顺序不影响含义；null 单一类型也不能被转成任意值。</summary>
    [Theory]
    [InlineData("\"null\"", "NULL", false)] [InlineData("[\"null\"]", "NULL", false)]
    [InlineData("[\"null\",\"string\"]", "STRING", true)] [InlineData("\"boolean\"", "BOOLEAN", false)]
    public void NativeTypeAndNullable_AreExplicit(string encoded, string expected, bool nullable)
    {
        var target = Convert(Source("{\"type\":" + encoded + "}"), LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, TypedOptions());
        var schema = target["generationConfig"]!["responseSchema"]!;
        Assert.Equal(expected, schema["type"]!.GetValue<string>());
        Assert.Equal(nullable, schema["nullable"]?.GetValue<bool>() ?? false);
    }

    /// <summary>非法限制、重复类型和空 anyOf 不得通过 typed 目标选项修复。</summary>
    [Theory]
    [InlineData("""{"type":"array","maxItems":"2"}""")]
    [InlineData("""{"type":"array","maxItems":1.5}""")]
    [InlineData("""{"type":"array","maxItems":9223372036854775808}""")]
    [InlineData("""{"type":["string","string"]}""")]
    [InlineData("""{"type":"object","anyOf":[]}""")]
    public void InvalidSchema_IsRejected(string schema)
        => Assert.Throws<ArgumentException>(() => Convert(Source(schema), LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, TypedOptions(true)));

    /// <summary>明确开放对象等价于 typed 的开放对象，不能把关闭对象也当成该情况。</summary>
    [Fact]
    public void OpenObject_RemainsOpenWithoutInventingClosure()
    {
        var target = Convert(Source("""{"type":"object","additionalProperties":true}"""), LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, TypedOptions());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"type":"OBJECT"}"""), target["generationConfig"]!["responseSchema"]));
    }

    /// <summary>typed 选项不覆盖同协议的完整复制契约。</summary>
    [Fact]
    public void NativeCopy_DoesNotMigrateEnvelope()
    {
        var source = Fixture()["Gemini"]!.AsObject(); var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, LlmProtocol.Gemini, TypedOptions());
        Assert.Equal(source.ToJsonString(), JsonNode.Parse(result.Data.GetRawText())!.ToJsonString());
    }

    /// <summary>只有 strict 文本结构化输出的已知顺序承诺传播到嵌套对象，不能用于普通宽松 JSON。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NestedOrdering_FollowsSourceGuarantee(bool strict)
    {
        var source = Request(LlmProtocol.Responses);
        SetFormat(source, LlmProtocol.Responses, JsonNode.Parse("""{"type":"object","properties":{"z":{"type":"object","properties":{"b":{"type":"boolean"},"a":{"type":"string"}}},"a":{"type":"integer"}}}""")!);
        source["text"]!["format"]!["strict"] = strict;
        var target = Convert(source, LlmProtocol.Responses, LlmProtocol.Gemini, TypedOptions(true));
        var schema = target["generationConfig"]!["responseSchema"]!;
        Assert.Equal(strict, schema.AsObject().ContainsKey("propertyOrdering"));
        Assert.Equal(strict, schema["properties"]!["z"]!.AsObject().ContainsKey("propertyOrdering"));
        if (!strict) return;
        Assert.Equal(new[] { "z", "a" }, schema["propertyOrdering"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(new[] { "b", "a" }, schema["properties"]!["z"]!["propertyOrdering"]!.AsArray().Select(item => item!.GetValue<string>()));
    }

    /// <summary>默认旧 JSON Schema 路径不受显式 typed 模式的关键字限制影响。</summary>
    [Fact]
    public void DefaultEnvelope_KeepsAllJsonSchemaConstraints()
    {
        var source = Source("""{"type":"object","additionalProperties":false,"$defs":{"n":{"type":"integer"}},"properties":{"n":{"$ref":"#/$defs/n"}}}""");
        var target = Convert(source, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini);
        Assert.True(JsonNode.DeepEquals(Schema(source, LlmProtocol.ClaudeMessages), Schema(target, LlmProtocol.Gemini)));
        Assert.False(target["generationConfig"]!.AsObject().ContainsKey("responseSchema"));
    }

    /// <summary>多个示例是注释损耗，必须明确同意，全部原文仍保留于 OriginalData。</summary>
    [Fact]
    public void MultipleExamples_RequireExplicitDiagnosticConsent()
    {
        var source = Source("""{"type":"string","examples":["first","second"]}""");
        Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, TypedOptions()));
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, TypedOptions(true));
        Assert.Equal("schema_examples_not_representable", Assert.Single(result.Diagnostics).Code);
        Assert.Equal("first", result.Data.GetProperty("generationConfig").GetProperty("responseSchema").GetProperty("example").GetString());
        Assert.Equal(2, result.OriginalData.GetProperty("output_config").GetProperty("format").GetProperty("schema").GetProperty("examples").GetArrayLength());
    }

    /// <summary>纯文本和 JSON-object 模式不因 typed 目标选项而创造一个 Schema。</summary>
    [Theory]
    [InlineData("text", "text/plain")] [InlineData("json_object", "application/json")]
    public void PlainModes_DoNotInventSchemas(string type, string mime)
    {
        var source = Request(LlmProtocol.ChatCompletions); source["response_format"] = new JsonObject { ["type"] = type };
        var target = Convert(source, LlmProtocol.ChatCompletions, LlmProtocol.Gemini, TypedOptions());
        Assert.Equal(mime, target["generationConfig"]!["responseMimeType"]!.GetValue<string>());
        Assert.False(target["generationConfig"]!.AsObject().ContainsKey("responseSchema"));
    }

    /// <summary>从独立嵌入样本读取完整报文。</summary>
    internal static JsonObject Fixture()
    {
        using var stream = typeof(GeminiTypedTargetTests).Assembly.GetManifestResourceStream(
            "Asgard.Loki.LLM.Conversion.Tests.Fixtures.schema.typed-target.json") ?? throw new InvalidDataException("typed 目标样本缺失。");
        return JsonNode.Parse(stream)!.AsObject();
    }
    /// <summary>Claude 不添加源协议未保证的属性顺序；OpenAI strict 结构化输出保留顺序。</summary>
    internal static JsonObject Expected(JsonObject fixture, LlmProtocol protocol)
        => fixture[protocol == LlmProtocol.ClaudeMessages ? "Gemini" : "GeminiOrdered"]!.AsObject();
    /// <summary>目标方言由调用者显式选择。</summary>
    internal static LlmConversionOptions TypedOptions(bool loss = false) => new() { GeminiOutputFormat = GeminiOutputFormat.LegacySchema, AllowLoss = loss };
    /// <summary>用无包装元数据损耗的 Claude 请求聚焦校验约束。</summary>
    private static JsonObject Source(string schema)
    {
        var source = Request(LlmProtocol.ClaudeMessages); SetFormat(source, LlmProtocol.ClaudeMessages, JsonNode.Parse(schema)!); return source;
    }
}
