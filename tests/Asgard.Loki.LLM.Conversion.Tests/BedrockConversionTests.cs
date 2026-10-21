using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>Nova/Converse 完整请求/响应、推理布局、Schema 和工具 ID 的显式转换。</summary>
public sealed class BedrockConversionTests
{
    /// <summary>两种原生外壳、两种明确布局和两个调用方向均比较完整目标。</summary>
    public static IEnumerable<object[]> Routes()
    {
        foreach (var version in new[] { "v1", "v2" }) foreach (var nested in new[] { false, true }) foreach (var nova in new[] { false, true })
            yield return [version, nested, nova];
    }

    /// <summary>完整金样保留媒体顺序、错误结果、反序同名工具关联和精确 Schema。</summary>
    [Theory] [MemberData(nameof(Routes))]
    public void CompleteRequestAndResponse_MatchIndependentGoldens(string version, bool nested, bool nova)
    {
        var converse = nested ? "ConverseNested" : "ConverseDirect";
        var source = BedrockConversionFixtures.Read(version, nova ? "Nova" : converse);
        var expected = BedrockConversionFixtures.Read(version, nova ? converse : "Nova");
        var from = nova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse; var to = nova ? LlmProtocol.BedrockConverse : LlmProtocol.BedrockNova;
        var options = BedrockConversionFixtures.Options(version, nested); var before = source.ToJsonString();
        var actual = LlmProtocolConverter.ConvertRequest(source, from, to, options);
        Equal(expected, actual.Data); Assert.Empty(actual.Diagnostics); Assert.Equal(before, source.ToJsonString());
        var replay = LlmProtocolConverter.ConvertRequest(Parse(actual.Data), to, from, options); Equal(source, replay.Data);
        var schema = actual.Data.GetProperty("toolConfig").GetProperty("tools")[0].GetProperty("toolSpec").GetProperty("inputSchema").GetProperty("json");
        Assert.Equal("9007199254740993", schema.GetProperty("properties").GetProperty("n").GetProperty("minimum").GetRawText());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var response = BedrockConversionFixtures.Read(version, nova ? "ConverseResponse" : "NovaResponse");
        var convertedResponse = LlmProtocolConverter.ConvertResponse(response, actual.Context!);
        Equal(BedrockConversionFixtures.Read(version, nova ? "NovaResponse" : "ConverseResponse"), convertedResponse.Data);
        Assert.Empty(convertedResponse.Diagnostics); Equal(response, convertedResponse.OriginalData);
        source["messages"] = new JsonArray(); response["usage"]!["inputTokens"] = 0;
        Assert.Equal(3, actual.OriginalData.GetProperty("messages").GetArrayLength());
        Assert.Equal(17, convertedResponse.OriginalData.GetProperty("usage").GetProperty("inputTokens").GetInt32());
    }

    /// <summary>未知根/嵌套字段逐字段诊断，不能被复制进未证明支持的目标；原文始终保留。</summary>
    [Theory] [InlineData(true, "root")] [InlineData(false, "root")]
    [InlineData(true, "system")] [InlineData(false, "system")]
    [InlineData(true, "inference")] [InlineData(false, "inference")]
    [InlineData(true, "tool")] [InlineData(false, "tool")]
    public void UnknownFields_RemainExplicitLosses(bool nova, string location)
    {
        var source = BedrockConversionFixtures.Read("v2", nova ? "Nova" : "ConverseDirect");
        var parent = location switch { "system" => source["system"]![0]!.AsObject(), "inference" => source["inferenceConfig"]!.AsObject(),
            "tool" => source["toolConfig"]!["tools"]![0]!["toolSpec"]!.AsObject(), _ => source };
        parent["future"] = JsonNode.Parse("""{"false":false,"nil":null,"integer":9007199254740993}""");
        var from = nova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse; var to = nova ? LlmProtocol.BedrockConverse : LlmProtocol.BedrockNova;
        var strict = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, from, to, BedrockConversionFixtures.Options()));
        Assert.Equal("unsupported_field", Assert.Single(strict.Diagnostics).Code);
        var actual = LlmProtocolConverter.ConvertRequest(source, from, to, BedrockConversionFixtures.Options(allowLoss: true));
        Assert.EndsWith(".future", Assert.Single(actual.Diagnostics).Path); Equal(source, actual.OriginalData);
        Assert.DoesNotContain("future", actual.Data.GetRawText());
    }

    /// <summary>low/medium/high 与关闭分别保留，不生成推理预算或调整 maxTokens。</summary>
    [Theory] [InlineData("low", false)] [InlineData("medium", false)] [InlineData("high", false)] [InlineData("disabled", true)]
    public void ReasoningIntent_PreservesExplicitModeAndEffort(string effort, bool disabled)
    {
        var source = BedrockConversionFixtures.Read("v2", "Nova");
        var reasoning = source["inferenceConfig"]!["reasoningConfig"]!.AsObject(); reasoning["type"] = disabled ? "disabled" : "enabled";
        if (disabled) reasoning.Remove("maxReasoningEffort"); else reasoning["maxReasoningEffort"] = effort;
        if (effort == "high") source["inferenceConfig"]!.AsObject().Remove("maxTokens");
        var actual = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.BedrockNova, LlmProtocol.BedrockConverse, BedrockConversionFixtures.Options());
        Equal(reasoning, actual.Data.GetProperty("additionalModelRequestFields").GetProperty("reasoningConfig"));
        if (effort == "high") Assert.False(actual.Data.GetProperty("inferenceConfig").TryGetProperty("maxTokens", out _));
        else Assert.Equal(8192, actual.Data.GetProperty("inferenceConfig").GetProperty("maxTokens").GetInt32());
        Assert.DoesNotContain("budget", actual.Data.GetRawText());
    }

    /// <summary>高强度与采样冲突时保留失败，不通过删参数或降级强度掩盖。</summary>
    [Theory] [InlineData("temperature")] [InlineData("topP")] [InlineData("topK")]
    public void HighReasoningSamplingConflict_AlwaysRejects(string field)
    {
        var source = BedrockConversionFixtures.Read("v2", "Nova"); source["inferenceConfig"]![field] = 0;
        source["inferenceConfig"]!["reasoningConfig"]!["maxReasoningEffort"] = "high";
        source["inferenceConfig"]!.AsObject().Remove("maxTokens");
        Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.BedrockNova,
            LlmProtocol.BedrockConverse, BedrockConversionFixtures.Options(allowLoss: true)));
        Assert.Equal(0, source["inferenceConfig"]![field]!.GetValue<int>());
    }

    /// <summary>不提供猜测外壳和包装的默认值；错位或重复模型参数即使允许损耗也失败。</summary>
    [Theory] [InlineData("format")] [InlineData("layout")] [InlineData("schema")] [InlineData("wrong-root")]
    [InlineData("wrong-nested")] [InlineData("duplicate")] [InlineData("base-config")]
    public void MissingOrConflictingDeclarations_FailExplicitly(string kind)
    {
        var nova = kind is "format" or "layout" or "schema";
        var source = BedrockConversionFixtures.Read("v2", nova ? "Nova" : "ConverseDirect");
        var options = BedrockConversionFixtures.Options(allowLoss: true);
        if (kind == "format") options = options with { BedrockNovaRequestFormat = BedrockNovaRequestFormat.Unspecified };
        if (kind == "layout") options = options with { BedrockConverseInferenceFormat = BedrockConverseInferenceFormat.Unspecified };
        if (kind == "schema") source["schemaVersion"] = "messages-v1";
        if (kind == "wrong-root") options = options with { BedrockConverseInferenceFormat = BedrockConverseInferenceFormat.NestedInferenceConfig };
        if (kind == "wrong-nested") source = BedrockConversionFixtures.Read("v2", "ConverseNested");
        if (kind == "duplicate") source["additionalModelRequestFields"]!["inferenceConfig"] = new JsonObject { ["reasoningConfig"] = new JsonObject { ["type"] = "disabled" } };
        if (kind == "base-config") source["inferenceConfig"]!["reasoningConfig"] = new JsonObject { ["type"] = "disabled" };
        var failure = Record.Exception(() => LlmProtocolConverter.ConvertRequest(source, nova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse,
            nova ? LlmProtocol.BedrockConverse : LlmProtocol.BedrockNova, options));
        if (kind is "format" or "layout" or "schema") Assert.IsType<ArgumentException>(failure);
        else Assert.Equal("bedrock_inference_layout_mismatch", Assert.Single(Assert.IsType<LlmConversionException>(failure).Diagnostics).Code);
    }

    /// <summary>缺失不补零，null 和错误数值类型也不被忽略。</summary>
    [Theory] [InlineData("inputTokens", "null")] [InlineData("outputTokens", "\"2\"")] [InlineData("totalTokens", "-1")]
    [InlineData("latencyMs", "null")] [InlineData("inputTokens", "0.5")]
    public void InvalidRequiredNativeCounters_DoNotBecomeSuccess(string field, string value)
    {
        var source = BedrockConversionFixtures.Read("v2", "NovaResponse");
        source[field == "latencyMs" ? "metrics" : "usage"]![field] = JsonNode.Parse(value);
        var failure = Record.Exception(() => LlmProtocolConverter.ConvertResponse(source, LlmProtocol.BedrockNova, LlmProtocol.BedrockConverse,
            BedrockConversionFixtures.Options(allowLoss: true)));
        if (value is "null" or "-1") Assert.IsType<ArgumentException>(failure);
        else if (value == "0.5") Assert.IsType<FormatException>(failure);
        else Assert.IsType<InvalidOperationException>(failure);
    }

    /// <summary>数值、空容器和同协议不透明数据不经过跨协议标准化。</summary>
    [Theory] [InlineData(LlmProtocol.BedrockNova)] [InlineData(LlmProtocol.BedrockConverse)]
    public void IdentityCopies_PreserveWholeNativeJsonAndStream(LlmProtocol protocol)
    {
        var source = JsonNode.Parse("""{"encrypted_content":"opaque","type":"error","future":null,"n":9007199254740993}""")!.AsObject();
        Equal(source, LlmProtocolConverter.ConvertRequest(source, protocol, protocol).Data);
        Equal(source, LlmProtocolConverter.ConvertResponse(source, protocol, protocol).Data);
        var converter = new LlmProtocolStreamConverter(protocol, protocol); Equal(source, Assert.Single(converter.Push(source)).Data); Assert.Empty(converter.Complete());
    }

    /// <summary>未知响应元数据有独立损耗诊断，原生终态标签和零值不被改写。</summary>
    [Theory] [InlineData("end_turn")] [InlineData("max_tokens")] [InlineData("content_filtered")] [InlineData("vendor.future")]
    public void NativeStopReasonAndUnknownMetadata_AreNeverNormalized(string reason)
    {
        var source = BedrockConversionFixtures.Read("v2", "NovaResponse"); source["output"]!["message"]!["content"] = new JsonArray(); source["stopReason"] = reason;
        source["usage"]!["outputTokens"] = 0; source["usage"]!["totalTokens"] = 17; source["metrics"]!["future"] = false;
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(source, LlmProtocol.BedrockNova, LlmProtocol.BedrockConverse));
        var result = LlmProtocolConverter.ConvertResponse(source, LlmProtocol.BedrockNova, LlmProtocol.BedrockConverse, new() { AllowLoss = true });
        Assert.Equal(reason, result.Data.GetProperty("stopReason").GetString()); Assert.Equal(0, result.Data.GetProperty("usage").GetProperty("outputTokens").GetInt32());
        Assert.Equal("$.metrics.future", Assert.Single(result.Diagnostics).Path); Equal(source, result.OriginalData);
    }

    /// <summary>完整 JSON 比较不使用生产写出器重新生成预期。</summary>
    internal static void Equal(JsonNode expected, JsonElement actual) => Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), actual), actual.GetRawText());
    /// <summary>只读取实际输出以便执行显式反向调用，不用于金样生成。</summary>
    private static JsonObject Parse(JsonElement actual) => JsonNode.Parse(actual.GetRawText())!.AsObject();
}
