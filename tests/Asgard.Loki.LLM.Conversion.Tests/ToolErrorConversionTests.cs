using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>明确错误格式保留原始工具身份、内容种类与错误语义，不猜测普通业务对象。</summary>
public sealed class ToolErrorConversionTests
{
    public static TheoryData<LlmProtocol, LlmToolErrorFormat> Directions => ToolErrorFixtures.Directions();
    /// <summary>完整请求和往返均与独立金样比较，并断言反序同名工具调用关联。</summary>
    [Theory] [MemberData(nameof(Directions))]
    public void CompleteRequestAndRoundtrip_MatchIndependentGoldens(LlmProtocol source, LlmToolErrorFormat format)
    {
        var request = ToolErrorFixtures.Read(source, format); var before = request.ToJsonString(); var target = ToolErrorFixtures.Target(source); var options = ToolErrorFixtures.Options(format);
        var converted = LlmProtocolConverter.ConvertRequest(request, source, target, options); var actual = CustomToolConversionTests.Read(converted);
        CustomToolConversionTests.Equal(ToolErrorFixtures.Read(target, format), actual); Assert.Empty(converted.Diagnostics); Assert.Equal(before, request.ToJsonString());
        CustomToolConversionTests.Equal(request, JsonNode.Parse(converted.OriginalData.GetRawText()));
        var error = ToolErrorFixtures.Result(actual, target); Assert.Equal("call_b", error[target == LlmProtocol.Gemini ? "id" : "tool_use_id"]!.GetValue<string>());
        if (target == LlmProtocol.ClaudeMessages) Assert.True(error["is_error"]!.GetValue<bool>()); else Assert.True(error["response"]!.AsObject().ContainsKey("error"));
        var back = LlmProtocolConverter.ConvertRequest(actual, target, source, options); CustomToolConversionTests.Equal(request, CustomToolConversionTests.Read(back));
    }
    /// <summary>文本格式对空串、JSON 外观、换行/NUL、组合字符均按原始字符串互转。</summary>
    [Theory] [InlineData("")] [InlineData("{\"error\":false}")] [InlineData("null")] [InlineData("e\u0301🌏\r\n\0")]
    public void TextProfile_DoesNotGuessJson(string value)
    {
        var source = ToolErrorFixtures.Read(LlmProtocol.ClaudeMessages, LlmToolErrorFormat.GeminiErrorText); ToolErrorFixtures.Result(source, LlmProtocol.ClaudeMessages)["content"] = value;
        var options = ToolErrorFixtures.Options(LlmToolErrorFormat.GeminiErrorText);
        var converted = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, options); var actual = CustomToolConversionTests.Read(converted);
        var response = ToolErrorFixtures.Result(actual, LlmProtocol.Gemini)["response"]!.AsObject(); Assert.Single(response); Assert.Equal(value, response["error"]!.GetValue<string>());
        CustomToolConversionTests.Equal(source, CustomToolConversionTests.Read(LlmProtocolConverter.ConvertRequest(actual, LlmProtocol.Gemini, LlmProtocol.ClaudeMessages, options)));
    }
    /// <summary>对象格式保留任何非 null 原生错误值及并列输出、未知成员和精确数值，不把数组当 Claude 富内容。</summary>
    [Theory] [InlineData("false")] [InlineData("0")] [InlineData("\"\"")] [InlineData("[]")] [InlineData("{\"n\":9007199254740993}")]
    public void ObjectProfile_RetainsErrorJsonKinds(string error)
    {
        var request = ToolErrorFixtures.Read(LlmProtocol.Gemini, LlmToolErrorFormat.GeminiResponseObject); var response = ToolErrorFixtures.Result(request, LlmProtocol.Gemini)["response"]!.AsObject(); response["error"] = JsonNode.Parse(error);
        var options = ToolErrorFixtures.Options(LlmToolErrorFormat.GeminiResponseObject);
        var converted = CustomToolConversionTests.Read(LlmProtocolConverter.ConvertRequest(request, LlmProtocol.Gemini, LlmProtocol.ClaudeMessages, options));
        var result = ToolErrorFixtures.Result(converted, LlmProtocol.ClaudeMessages); Assert.True(result["is_error"]!.GetValue<bool>()); CustomToolConversionTests.Equal(response, JsonNode.Parse(result["content"]!.GetValue<string>()));
        CustomToolConversionTests.Equal(request, CustomToolConversionTests.Read(LlmProtocolConverter.ConvertRequest(converted, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, options)));
    }
    /// <summary>错误文本或完整 JSON 的实际 UTF-8 边界保持精确，普通损耗许可不能跳过。</summary>
    [Theory] [InlineData(LlmProtocol.ClaudeMessages, LlmToolErrorFormat.GeminiErrorText)] [InlineData(LlmProtocol.Gemini, LlmToolErrorFormat.GeminiErrorText)]
    [InlineData(LlmProtocol.ClaudeMessages, LlmToolErrorFormat.GeminiResponseObject)] [InlineData(LlmProtocol.Gemini, LlmToolErrorFormat.GeminiResponseObject)]
    public void ErrorSizeLimit_IsExact(LlmProtocol source, LlmToolErrorFormat format)
    {
        var request = ToolErrorFixtures.Read(source, format); var item = ToolErrorFixtures.Result(request, source);
        var raw = source == LlmProtocol.ClaudeMessages ? item["content"]!.GetValue<string>() : format == LlmToolErrorFormat.GeminiErrorText ? item["response"]!["error"]!.GetValue<string>() : item["response"]!.ToJsonString();
        var maximum = Encoding.UTF8.GetByteCount(raw); var options = ToolErrorFixtures.Options(format) with { MaxToolErrorBytes = maximum, AllowLoss = true };
        Assert.Empty(LlmProtocolConverter.ConvertRequest(request, source, ToolErrorFixtures.Target(source), options).Diagnostics);
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, source, ToolErrorFixtures.Target(source), options with { MaxToolErrorBytes = maximum - 1 }));
        Assert.Equal("tool_result_error_limit", Assert.Single(error.Diagnostics).Code);
    }
}
