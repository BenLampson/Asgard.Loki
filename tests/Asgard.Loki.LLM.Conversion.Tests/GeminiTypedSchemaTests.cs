using System.Text.Json.Nodes;
using Xunit;
using static Asgard.Loki.LLM.Conversion.Tests.GenerationSettingsTests;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>typed Schema 的类型、约束、数值和顺序必须显式转换，不能靠删字段通过。</summary>
public sealed class GeminiTypedSchemaTests
{
    /// <summary>完整原生请求与三个独立手工目标快照比较，原始对象保持不变。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)]
    public void CompleteRequest_MatchesIndependentSnapshot(LlmProtocol target)
    {
        var fixture = Fixture(); var source = fixture["Gemini"]!.AsObject(); var before = source.ToJsonString();
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, target, new() { DefaultSchemaName = "result" });
        Assert.Empty(result.Diagnostics);
        Assert.True(JsonNode.DeepEquals(fixture[target.ToString()], JsonNode.Parse(result.Data.GetRawText())), result.Data.GetRawText());
        Assert.Equal(before, source.ToJsonString());
        Assert.Equal(before, JsonNode.Parse(result.OriginalData.GetRawText())!.ToJsonString());
    }

    /// <summary>int64 数字与字符串指数都精确转换，不能通过 double/decimal 舍入小数。</summary>
    [Theory]
    [InlineData("\"9223372036854775807\"", long.MaxValue)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("\"9.223372036854775807e18\"", long.MaxValue)]
    [InlineData("9.223372036854775807e18", long.MaxValue)]
    [InlineData("\"2000e-3\"", 2)] [InlineData("\"1.000\"", 1)]
    [InlineData("\"0e999999999999999999999999\"", 0)] [InlineData("\"-0\"", 0)]
    [InlineData("\"001\"", 1)] [InlineData("0", 0)]
    public void IntegerLimits_AreExact(string encoded, long expected)
    {
        var source = TypedRequest("{\"type\":\"ARRAY\",\"maxItems\":" + encoded + "}");
        var target = Convert(source, LlmProtocol.Gemini, LlmProtocol.Responses, Options());
        Assert.Equal(expected, Schema(target, LlmProtocol.Responses)["maxItems"]!.GetValue<long>());
    }

    /// <summary>任何不能精确表示的限制均在允许损耗时仍失败。</summary>
    [Theory]
    [InlineData("\"9223372036854775808\"")] [InlineData("-1")] [InlineData("\"1e-1\"")]
    [InlineData("\"1.000000000000000000000000000001\"")] [InlineData("true")]
    [InlineData("\"1e999999999999999999999\"")] [InlineData("\"\"")] [InlineData("\"1e-999999999999999999999\"")]
    public void InvalidIntegerLimits_AreNeverRounded(string encoded)
    {
        var source = TypedRequest("{\"type\":\"ARRAY\",\"minItems\":" + encoded + "}");
        Assert.Throws<ArgumentException>(() => Convert(source, LlmProtocol.Gemini, LlmProtocol.Responses, Options(true)));
    }

    /// <summary>官方 protobuf 类型数字和大小写类型名都保持对应的 JSON 类型。</summary>
    [Theory]
    [InlineData("1", "string")] [InlineData("2", "number")] [InlineData("3", "integer")]
    [InlineData("4", "boolean")] [InlineData("5", "array")] [InlineData("6", "object")] [InlineData("7", "null")]
    [InlineData("\"OBJECT\"", "object")] [InlineData("\"object\"", "object")]
    public void NativeTypes_AreExplicit(string encoded, string expected)
    {
        var target = Convert(TypedRequest("{\"type\":" + encoded + "}"), LlmProtocol.Gemini, LlmProtocol.Responses, Options());
        Assert.Equal(expected, Schema(target, LlmProtocol.Responses)["type"]!.GetValue<string>());
    }

    /// <summary>nullable 加入类型的 null，不改变 enum 或 anyOf 等其他校验的限制。</summary>
    [Fact]
    public void Nullable_DoesNotInventEnumValues()
    {
        var target = Convert(TypedRequest("""{"type":"STRING","nullable":true,"enum":["yes"],"default":null,"example":null}"""),
            LlmProtocol.Gemini, LlmProtocol.Responses, Options());
        var schema = Schema(target, LlmProtocol.Responses);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""["string","null"]"""), schema["type"]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""["yes"]"""), schema["enum"]));
        Assert.True(schema.AsObject().ContainsKey("default")); Assert.Null(schema["default"]);
        Assert.Single(schema["examples"]!.AsArray()); Assert.Null(schema["examples"]![0]);
    }

    /// <summary>完整属性顺序由 OpenAI 的 properties 顺序表达，不能只用无序快照断言。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)]
    public void Ordering_IsPreservedForOpenAi(LlmProtocol target)
    {
        var source = OrderedRequest(); var before = source.ToJsonString();
        var result = Convert(source, LlmProtocol.Gemini, target, Options());
        Assert.Equal(new[] { "b", "a" }, Schema(result, target)["properties"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(before, source.ToJsonString());
    }

    /// <summary>Claude 或不完整顺序不能声称输出顺序等价；允许损耗仍返回精确诊断。</summary>
    [Theory]
    [InlineData(LlmProtocol.ClaudeMessages, false)] [InlineData(LlmProtocol.Responses, true)]
    public void UnrepresentableOrdering_RequiresLossConsent(LlmProtocol target, bool partial)
    {
        var source = OrderedRequest();
        if (partial) source["generationConfig"]!["responseSchema"]!["propertyOrdering"]!.AsArray().RemoveAt(1);
        Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.Gemini, target, Options()));
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, target, Options(true));
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("schema_property_ordering_not_representable", diagnostic.Code);
        Assert.Equal("$.generationConfig.responseSchema.propertyOrdering", diagnostic.Path);
    }

    /// <summary>未知校验关键字不能被 AllowLoss 删除，即使位于嵌套对象且值为 null。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnknownConstraint_IsHardFailure(bool allowLoss)
    {
        var source = TypedRequest("""{"type":"OBJECT","properties":{"n":{"type":"INTEGER","futureConstraint":null}}}""");
        var failure = Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.Gemini, LlmProtocol.Responses, Options(allowLoss)));
        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal("unsupported_schema_keyword", diagnostic.Code);
        Assert.Equal("$.generationConfig.responseSchema.properties[\"n\"].futureConstraint", diagnostic.Path);
    }

    /// <summary>不明类型不得被放宽成任意 JSON 值；同协议复制仍保留未来原生结构。</summary>
    [Theory]
    [InlineData("0")] [InlineData("8")] [InlineData("\"TYPE_UNSPECIFIED\"")] [InlineData("\"FUTURE\"")]
    public void UnknownType_IsNativeOnly(string type)
    {
        var source = TypedRequest("{\"type\":" + type + "}");
        Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.Gemini, LlmProtocol.Responses, Options(true)));
        var copy = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, LlmProtocol.Gemini);
        Assert.Equal(source.ToJsonString(), JsonNode.Parse(copy.Data.GetRawText())!.ToJsonString());
    }

    /// <summary>原生普通 null 字段按 ProtoJSON 未设置语义处理，不能生成非法空类型或约束。</summary>
    [Fact]
    public void ProtoNullsAndEmptyRepeatedFields_KeepUnsetSemantics()
    {
        var source = TypedRequest("""{"type":"STRING","nullable":null,"minLength":null,"enum":[],"anyOf":[],"propertyOrdering":null}""");
        var target = Convert(source, LlmProtocol.Gemini, LlmProtocol.Responses, Options());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"type":"string"}"""), Schema(target, LlmProtocol.Responses)));
    }

    /// <summary>缺少类型、无效枚举和重复/未知顺序名属于非法结构，不能通过损耗开关修复。</summary>
    [Theory]
    [InlineData("{}")] [InlineData("""{"type":null}""")]
    [InlineData("""{"type":"INTEGER","enum":["1"]}""")]
    [InlineData("""{"type":"OBJECT","properties":{"a":{"type":"STRING"}},"propertyOrdering":["a","a"]}""")]
    [InlineData("""{"type":"OBJECT","properties":{},"propertyOrdering":["a"]}""")]
    public void InvalidTypedStructures_AreRejected(string schema)
        => Assert.Throws<ArgumentException>(() => Convert(TypedRequest(schema), LlmProtocol.Gemini, LlmProtocol.Responses, Options(true)));

    /// <summary>原始 protobuf 字段名同样有效；空值与零值不得通过别名跳过校验。</summary>
    [Fact]
    public void ProtoFieldNames_AreSupported()
    {
        var source = TypedRequest("""{"type":"OBJECT","min_properties":"0","max_properties":"2","properties":{"a":{"type":"ARRAY","min_items":"0","max_items":"20","items":{"type":"STRING","min_length":"0","max_length":"2"}},"b":{"type":"BOOLEAN"}},"property_ordering":["b","a"],"any_of":[{"type":"OBJECT"}]}""");
        var schema = Schema(Convert(source, LlmProtocol.Gemini, LlmProtocol.Responses, Options()), LlmProtocol.Responses);
        Assert.Equal(0, schema["minProperties"]!.GetValue<long>()); Assert.Equal(2, schema["maxProperties"]!.GetValue<long>());
        Assert.Equal(20, schema["properties"]!["a"]!["maxItems"]!.GetValue<long>());
        Assert.Equal(2, schema["properties"]!["a"]!["items"]!["maxLength"]!.GetValue<long>());
        Assert.Equal(new[] { "b", "a" }, schema["properties"]!.AsObject().Select(item => item.Key));
        Assert.Single(schema["anyOf"]!.AsArray());
    }

    /// <summary>camel/snake 别名同时存在属于冲突输入，不能选择较弱约束。</summary>
    [Fact]
    public void DuplicateProtoAliases_AreRejected()
        => Assert.Throws<ArgumentException>(() => Convert(TypedRequest("""{"type":"ARRAY","minItems":"1","min_items":"0"}"""),
            LlmProtocol.Gemini, LlmProtocol.Responses, Options(true)));

    /// <summary>原生 double 限制不溢出，字符串数值仍保留数字精度与零。</summary>
    [Theory]
    [InlineData("\"0\"", "0")] [InlineData("\"1.2345678901234567\"", "1.2345678901234567")]
    public void NumericBounds_KeepOriginalDigits(string encoded, string expected)
    {
        var schema = Schema(Convert(TypedRequest("{\"type\":\"NUMBER\",\"minimum\":" + encoded + "}"),
            LlmProtocol.Gemini, LlmProtocol.Responses, Options()), LlmProtocol.Responses);
        Assert.Equal(expected, schema["minimum"]!.ToJsonString());
    }

    /// <summary>无限上界不是合法的有限 JSON Schema 数字。</summary>
    [Fact]
    public void NonFiniteBounds_AreRejected()
        => Assert.Throws<ArgumentException>(() => Convert(TypedRequest("""{"type":"NUMBER","maximum":1e999}"""),
            LlmProtocol.Gemini, LlmProtocol.Responses, Options(true)));

    /// <summary>独立嵌入样本用于纯转换及公开 SDK 的最终线上报文断言。</summary>
    internal static JsonObject Fixture()
    {
        using var stream = typeof(GeminiTypedSchemaTests).Assembly.GetManifestResourceStream(
            "Asgard.Loki.LLM.Conversion.Tests.Fixtures.schema.typed-source.json") ?? throw new InvalidDataException("typed Schema 样本缺失。");
        return JsonNode.Parse(stream)!.AsObject();
    }

    /// <summary>构造最小合法的原生 Gemini typed Schema 请求。</summary>
    private static JsonObject TypedRequest(string schema)
    {
        var source = Request(LlmProtocol.Gemini);
        source["generationConfig"] = new JsonObject { ["responseMimeType"] = "application/json", ["responseSchema"] = JsonNode.Parse(schema) };
        return source;
    }

    /// <summary>属性初始顺序与指定顺序不同，保证测试能发现转换遗漏。</summary>
    private static JsonObject OrderedRequest() => TypedRequest("""{"type":"OBJECT","properties":{"a":{"type":"STRING"},"b":{"type":"BOOLEAN"}},"propertyOrdering":["b","a"]}""");

    /// <summary>目标名称和 Claude 必需上限由调用者显式提供。</summary>
    private static LlmConversionOptions Options(bool allowLoss = false) => new() { DefaultSchemaName = "result", DefaultMaxTokens = 4096, AllowLoss = allowLoss };
}
