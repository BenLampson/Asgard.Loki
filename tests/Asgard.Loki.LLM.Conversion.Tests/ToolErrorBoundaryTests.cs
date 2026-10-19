using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>错误不能被普通损耗同意抹除，声明格式、身份或目标不一致均硬失败。</summary>
public sealed class ToolErrorBoundaryTests
{
    /// <summary>Gemini 根级错误以前会被当成功文本，默认现在与 Claude 一样需要显式格式。</summary>
    [Theory] [InlineData(LlmProtocol.ClaudeMessages, false)] [InlineData(LlmProtocol.ClaudeMessages, true)] [InlineData(LlmProtocol.Gemini, false)] [InlineData(LlmProtocol.Gemini, true)]
    public void DefaultPolicy_DoesNotLoseErrorSemantics(LlmProtocol source, bool allowLoss)
    {
        var request = ToolErrorFixtures.Read(source, LlmToolErrorFormat.GeminiResponseObject);
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, source, ToolErrorFixtures.Target(source), new() { AllowLoss = allowLoss }));
        Assert.Equal("tool_result_error_not_representable", Assert.Single(error.Diagnostics).Code);
    }
    /// <summary>Chat/Responses 没有等价错误标志，两个格式都不能授权降级。</summary>
    [Theory] [InlineData(LlmProtocol.ClaudeMessages, LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages, LlmProtocol.Responses)]
    [InlineData(LlmProtocol.Gemini, LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Gemini, LlmProtocol.Responses)]
    public void UnsupportedTarget_DoesNotTreatErrorAsSuccess(LlmProtocol source, LlmProtocol target)
    {
        foreach (var format in new[] { LlmToolErrorFormat.GeminiResponseObject, LlmToolErrorFormat.GeminiErrorText })
        {
            var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(ToolErrorFixtures.Read(source, format), source, target, ToolErrorFixtures.Options(format) with { AllowLoss = true }));
            Assert.Equal("tool_result_error_not_representable", Assert.Single(error.Diagnostics).Code);
        }
    }
    /// <summary>缺失或 null error、非对象、重复属性和不完整 JSON 不能成为声称等价的错误。</summary>
    [Theory] [InlineData("{}")] [InlineData("{\"error\":null}")] [InlineData("[]")] [InlineData("null")] [InlineData("not json")]
    [InlineData("{\"error\":1,\"error\":2}")] [InlineData("{\"error\":")]
    public void InvalidObjectProfile_IsRejected(string content)
    {
        var request = ToolErrorFixtures.Read(LlmProtocol.ClaudeMessages, LlmToolErrorFormat.GeminiResponseObject); ToolErrorFixtures.Result(request, LlmProtocol.ClaudeMessages)["content"] = content;
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, ToolErrorFixtures.Options(LlmToolErrorFormat.GeminiResponseObject) with { AllowLoss = true }));
        Assert.Equal("tool_result_error_format_not_representable", Assert.Single(error.Diagnostics).Code);
    }
    /// <summary>文本格式只接受一个字符串 error，不能删掉额外输出/未知字段或串化结构错误。</summary>
    [Theory] [InlineData("{\"error\":null}")] [InlineData("{\"error\":{}}")] [InlineData("{\"error\":[]}")]
    [InlineData("{\"error\":false}")] [InlineData("{\"error\":\"bad\",\"output\":null}")] [InlineData("{\"error\":\"bad\",\"future\":false}")]
    public void AmbiguousTextProfile_IsRejected(string response)
    {
        var request = ToolErrorFixtures.Read(LlmProtocol.Gemini, LlmToolErrorFormat.GeminiErrorText); ToolErrorFixtures.Result(request, LlmProtocol.Gemini)["response"] = JsonNode.Parse(response);
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.Gemini, LlmProtocol.ClaudeMessages, ToolErrorFixtures.Options(LlmToolErrorFormat.GeminiErrorText) with { AllowLoss = true }));
        Assert.Equal("tool_result_error_format_not_representable", Assert.Single(error.Diagnostics).Code);
    }
    /// <summary>普通字符串结果中的根级 error 不代表源协议声明工具失败；false/省略标记均拒绝意外升级。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ClaudeSuccess_WithErrorKeyIsNotImplicitFailure(bool explicitFalse)
    {
        var request = ToolErrorFixtures.Read(LlmProtocol.ClaudeMessages, LlmToolErrorFormat.GeminiResponseObject); var result = ToolErrorFixtures.Result(request, LlmProtocol.ClaudeMessages);
        if (explicitFalse) result["is_error"] = false; else result.Remove("is_error");
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, ToolErrorFixtures.Options(LlmToolErrorFormat.GeminiResponseObject) with { AllowLoss = true }));
        Assert.Equal("tool_result_error_semantics_conflict", Assert.Single(error.Diagnostics).Code);
    }
    /// <summary>显式错误数组不会被当普通 JSON 数组或串接文本，新格式仍不提供媒体容器映射。</summary>
    [Theory] [InlineData(LlmToolErrorFormat.GeminiResponseObject)] [InlineData(LlmToolErrorFormat.GeminiErrorText)]
    public void RichErrorArray_RemainsExplicitlyUnsupported(LlmToolErrorFormat format)
    {
        var request = ToolErrorFixtures.Read(LlmProtocol.ClaudeMessages, format); ToolErrorFixtures.Result(request, LlmProtocol.ClaudeMessages)["content"] = JsonNode.Parse("[{\"type\":\"text\",\"text\":\"bad\"}]");
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, ToolErrorFixtures.Options(format) with { AllowLoss = true }));
    }
    /// <summary>格式不能绕过原始工具关联；孤儿、重复消费和错误名称都必须失败。</summary>
    [Theory] [InlineData("orphan")] [InlineData("duplicate")] [InlineData("name")] [InlineData("no-id")]
    public void GeminiErrorAssociation_IsChecked(string kind)
    {
        var request = ToolErrorFixtures.Read(LlmProtocol.Gemini, LlmToolErrorFormat.GeminiErrorText); var result = ToolErrorFixtures.Result(request, LlmProtocol.Gemini);
        if (kind == "orphan") result["id"] = "missing"; else if (kind == "name") result["name"] = "other"; else if (kind == "no-id") result.Remove("id");
        else request["contents"]![2]!["parts"]!.AsArray().Add(request["contents"]![2]!["parts"]![0]!.DeepClone());
        Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.Gemini, LlmProtocol.ClaudeMessages, ToolErrorFixtures.Options(LlmToolErrorFormat.GeminiErrorText)));
    }
    /// <summary>无效 Unicode 不能经序列化替换；错误内容与属性名均检查。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void InvalidUnicode_IsRejectedBeforeSerialization(bool property)
    {
        var request = ToolErrorFixtures.Read(LlmProtocol.Gemini, LlmToolErrorFormat.GeminiResponseObject); var response = ToolErrorFixtures.Result(request, LlmProtocol.Gemini)["response"]!.AsObject();
        if (property) response["\ud800"] = 0; else response["error"] = "\ud800";
        Assert.Throws<EncoderFallbackException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.Gemini, LlmProtocol.ClaudeMessages, ToolErrorFixtures.Options(LlmToolErrorFormat.GeminiResponseObject)));
    }
    /// <summary>原生复制不应用跨协议格式、不解析错误，也不删除未来字段。</summary>
    [Theory] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void NativeCopy_IsUntouched(LlmProtocol source)
    {
        var request = ToolErrorFixtures.Read(source, LlmToolErrorFormat.GeminiResponseObject); ToolErrorFixtures.Result(request, source)["future"] = null;
        var result = LlmProtocolConverter.ConvertRequest(request, source, source); Assert.Empty(result.Diagnostics); CustomToolConversionTests.Equal(request, CustomToolConversionTests.Read(result));
    }
    /// <summary>普通 OpenAI 结果对象的 error 键也不能在 Gemini 被隐式升级为失败。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions, false)] [InlineData(LlmProtocol.ChatCompletions, true)] [InlineData(LlmProtocol.Responses, false)] [InlineData(LlmProtocol.Responses, true)]
    public void OrdinaryOpenAiResult_IsNotUpgradedToGeminiError(LlmProtocol source, bool allowLoss)
    {
        var request = RichToolResultFixtures.Read(source); RichToolResultFixtures.SetContent(request, source, JsonValue.Create("{\"error\":\"business\"}"));
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, source, LlmProtocol.Gemini, new() { AllowLoss = allowLoss }));
        Assert.Equal("tool_result_error_semantics_conflict", Assert.Single(error.Diagnostics).Code);
    }
    /// <summary>非法格式或资源配置在请求、完整响应与流构造入口均提前拒绝。</summary>
    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(1)]
    public void InvalidConfiguration_IsRejected(int kind)
    {
        var options = kind == 1 ? new LlmConversionOptions { ToolErrorFormat = (LlmToolErrorFormat)999 } : new() { MaxToolErrorBytes = kind };
        Assert.Throws<ArgumentOutOfRangeException>(() => LlmProtocolConverter.ConvertRequest(new(), LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, options));
        Assert.Throws<ArgumentOutOfRangeException>(() => LlmProtocolConverter.ConvertResponse(new(), LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, options));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmProtocolStreamConverter(LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, options));
    }
    /// <summary>同步错误格式不能隐藏原生持续回填、调度或媒体结果，即使显式允许普通显示元数据损耗。</summary>
    [Theory] [InlineData("parts", "[]")] [InlineData("parts", "[{\"inlineData\":{\"mimeType\":\"image/png\",\"data\":\"aGVsbG8=\"}}]")]
    [InlineData("willContinue", "false")] [InlineData("willContinue", "true")] [InlineData("scheduling", "\"WHEN_IDLE\"")] [InlineData("scheduling", "\"SILENT\"")]
    public void NativeResultLifecycleAndMedia_CannotBeDiscarded(string key, string value)
    {
        var request = ToolErrorFixtures.Read(LlmProtocol.Gemini, LlmToolErrorFormat.GeminiResponseObject); ToolErrorFixtures.Result(request, LlmProtocol.Gemini)[key] = JsonNode.Parse(value);
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.Gemini, LlmProtocol.ClaudeMessages, ToolErrorFixtures.Options(LlmToolErrorFormat.GeminiResponseObject) with { AllowLoss = true }));
        Assert.Contains(error.Diagnostics, item => item.Code == "tool_result_error_controls_not_representable");
    }
    /// <summary>未知普通壳元数据仍需诊断，显式同意后原始 JSON 保留它，目标错误标志不能丢。</summary>
    [Theory] [InlineData("null")] [InlineData("false")]
    public void UnknownShellMetadata_IsDiagnosedAndRetained(string value)
    {
        var request = ToolErrorFixtures.Read(LlmProtocol.Gemini, LlmToolErrorFormat.GeminiResponseObject); ToolErrorFixtures.Result(request, LlmProtocol.Gemini)["future"] = JsonNode.Parse(value);
        var options = ToolErrorFixtures.Options(LlmToolErrorFormat.GeminiResponseObject);
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.Gemini, LlmProtocol.ClaudeMessages, options));
        var result = LlmProtocolConverter.ConvertRequest(request, LlmProtocol.Gemini, LlmProtocol.ClaudeMessages, options with { AllowLoss = true });
        Assert.EndsWith(".future", Assert.Single(result.Diagnostics).Path); Assert.True(ToolErrorFixtures.Result(CustomToolConversionTests.Read(result), LlmProtocol.ClaudeMessages)["is_error"]!.GetValue<bool>());
        CustomToolConversionTests.Equal(request, JsonNode.Parse(result.OriginalData.GetRawText()));
    }
}
