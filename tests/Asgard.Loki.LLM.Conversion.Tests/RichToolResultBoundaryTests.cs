using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>富结果必须保持错误和不透明状态边界，不能省略未知内容后伪装为成功执行。</summary>
public sealed class RichToolResultBoundaryTests
{
    /// <summary>字符串和数组结果均不能把明确错误标记删掉，即使显式允许普通字段损耗。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions, false)] [InlineData(LlmProtocol.Responses, false)] [InlineData(LlmProtocol.Gemini, false)]
    [InlineData(LlmProtocol.ChatCompletions, true)] [InlineData(LlmProtocol.Responses, true)] [InlineData(LlmProtocol.Gemini, true)]
    public void ExplicitToolError_IsNotConvertedToSuccess(LlmProtocol target, bool array)
    {
        var source = RichToolResultFixtures.Read(LlmProtocol.ClaudeMessages); var result = RichToolResultFixtures.Result(source, LlmProtocol.ClaudeMessages);
        result["is_error"] = true; if (!array) result["content"] = "failed"; var before = source.ToJsonString();
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, target, new() { AllowLoss = true }));
        Assert.Equal("tool_result_error_not_representable", Assert.Single(error.Diagnostics).Code); Assert.Equal(before, source.ToJsonString());
    }

    /// <summary>非法错误标志不被当作 false，null 和字符串必须失败。</summary>
    [Theory] [InlineData("null")] [InlineData("\"false\"")]
    public void InvalidErrorFlag_IsNotDefaulted(string value)
    {
        var source = RichToolResultFixtures.Read(LlmProtocol.ClaudeMessages); RichToolResultFixtures.Result(source, LlmProtocol.ClaudeMessages)["is_error"] = JsonNode.Parse(value);
        void Convert() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses, new() { AllowLoss = true });
        if (value == "null") Assert.Throws<ArgumentException>(Convert); else Assert.Throws<InvalidOperationException>(Convert);
    }

    /// <summary>Chat/Gemini 无对应媒体结果数组，不能把数组 JSON 化或发成普通用户消息。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions, LlmProtocol.Gemini, false)]
    [InlineData(LlmProtocol.Responses, LlmProtocol.Gemini, false)] [InlineData(LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, false)]
    [InlineData(LlmProtocol.Responses, LlmProtocol.ChatCompletions, true)] [InlineData(LlmProtocol.ClaudeMessages, LlmProtocol.ChatCompletions, true)]
    public void UnrepresentableResultContainer_IsAlwaysExplicit(LlmProtocol sourceProtocol, LlmProtocol target, bool media)
    {
        var source = RichToolResultFixtures.Read(sourceProtocol, media);
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, sourceProtocol, target, new() { AllowLoss = true }));
        Assert.Equal("rich_tool_result_not_representable", Assert.Single(error.Diagnostics).Code);
    }

    /// <summary>不支持的原生内容种类和嵌套结果不得通过普通损耗开关被默默省略。</summary>
    [Theory] [InlineData("future")] [InlineData("tool_result")] [InlineData("tool_use")] [InlineData("thinking")]
    public void UnsupportedContentKind_IsNotStringified(string type)
    {
        var source = RichToolResultFixtures.Read(LlmProtocol.ClaudeMessages); RichToolResultFixtures.Parts(source, LlmProtocol.ClaudeMessages).Add(new JsonObject { ["type"] = type, ["text"] = "opaque" });
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses, new() { AllowLoss = true }));
        Assert.Equal("unsupported_tool_result_content", Assert.Single(error.Diagnostics).Code);
    }

    /// <summary>结果不是字符串、数组时，不能自动转换其业务数据类型。</summary>
    [Theory] [InlineData("null")] [InlineData("true")] [InlineData("0")] [InlineData("{\"ok\":true}")]
    public void InvalidResultValue_IsNeverCoerced(string value)
    {
        var source = RichToolResultFixtures.Read(LlmProtocol.Responses); RichToolResultFixtures.SetContent(source, LlmProtocol.Responses, JsonNode.Parse(value));
        Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions, new() { AllowLoss = true }));
    }

    /// <summary>原生字符串仍是字符串，不因看起来像 JSON 或数组而改变内容种类。</summary>
    [Theory] [InlineData("")] [InlineData("{\"ok\":false}")] [InlineData("[]")]
    public void StringResult_IsNotReparsedAsRichContent(string value)
    {
        var source = RichToolResultFixtures.Read(LlmProtocol.Responses); RichToolResultFixtures.SetContent(source, LlmProtocol.Responses, JsonValue.Create(value));
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions);
        Assert.Empty(result.Diagnostics); Assert.Equal(value, RichToolResultFixtures.Result(CustomToolConversionTests.Read(result), LlmProtocol.ChatCompletions)["content"]!.GetValue<string>());
    }

    /// <summary>内联图片即使发往不解码图片的目标，也不能接受空数据或损坏 Base64。</summary>
    [Theory] [InlineData("")] [InlineData("bad!")] [InlineData("_-8=")]
    public void InvalidInlineImage_IsNotForwarded(string value)
    {
        var source = RichToolResultFixtures.Read(LlmProtocol.ClaudeMessages, true); RichToolResultFixtures.Parts(source, LlmProtocol.ClaudeMessages)[1]!["source"]!["data"] = value;
        Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses, new() { AllowLoss = true }));
    }

    /// <summary>已识别文本块内部的未知字段必须仍有诊断，false/null 不自动当省略。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)]
    public void UnknownNestedField_IsDiagnosedAndRetained(LlmProtocol protocol)
    {
        var source = RichToolResultFixtures.Read(protocol); RichToolResultFixtures.Parts(source, protocol)[0]!["future"] = null;
        var target = protocol == LlmProtocol.Responses ? LlmProtocol.ChatCompletions : LlmProtocol.Responses;
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, protocol, target));
        var result = LlmProtocolConverter.ConvertRequest(source, protocol, target, new() { AllowLoss = true });
        Assert.EndsWith(".future", Assert.Single(result.Diagnostics).Path); CustomToolConversionTests.Equal(source, JsonNode.Parse(result.OriginalData.GetRawText()));
    }

    /// <summary>富内容不能绕过工具 ID 的唯一关联和一次消费规则。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions, false)] [InlineData(LlmProtocol.Responses, false)] [InlineData(LlmProtocol.ClaudeMessages, false)]
    [InlineData(LlmProtocol.ChatCompletions, true)] [InlineData(LlmProtocol.Responses, true)] [InlineData(LlmProtocol.ClaudeMessages, true)]
    public void OrphanOrDuplicateResult_IsRejected(LlmProtocol protocol, bool duplicate)
    {
        var source = RichToolResultFixtures.Read(protocol); var result = RichToolResultFixtures.Result(source, protocol);
        if (duplicate)
        {
            if (protocol == LlmProtocol.Responses) source["input"]!.AsArray().Add(result.DeepClone());
            else if (protocol == LlmProtocol.ChatCompletions) source["messages"]!.AsArray().Add(result.DeepClone());
            else source["messages"]![2]!["content"]!.AsArray().Add(result.DeepClone());
        }
        else result[protocol == LlmProtocol.Responses ? "call_id" : protocol == LlmProtocol.ChatCompletions ? "tool_call_id" : "tool_use_id"] = "orphan";
        var target = protocol == LlmProtocol.Responses ? LlmProtocol.ChatCompletions : LlmProtocol.Responses;
        Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(source, protocol, target, new() { AllowLoss = true }));
    }

    /// <summary>未回填的工具轮次之间出现用户/assistant 文本，不能生成违反 Chat 协议顺序的报文。</summary>
    [Theory] [InlineData("user", false)] [InlineData("assistant", false)] [InlineData("user", true)] [InlineData("assistant", true)]
    public void MessageBeforePendingResult_IsNotReordered(string role, bool allowLoss)
    {
        var source = RichToolResultFixtures.Read(LlmProtocol.Responses); source["input"]!.AsArray().Insert(2, new JsonObject { ["type"] = "message", ["role"] = role,
            ["content"] = new JsonArray(new JsonObject { ["type"] = role == "assistant" ? "output_text" : "input_text", ["text"] = "interleaved" }) });
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions, new() { AllowLoss = allowLoss }));
        Assert.Equal("tool_turn_order_not_representable", Assert.Single(error.Diagnostics).Code);
    }

    /// <summary>同一 Claude 用户消息里前置的普通文本也不能绕过待回填状态校验。</summary>
    [Fact]
    public void TextBeforeToolResult_IsNotMovedAfterIt()
    {
        var source = RichToolResultFixtures.Read(LlmProtocol.ClaudeMessages); source["messages"]![2]!["content"]!.AsArray().Insert(0, new JsonObject { ["type"] = "text", ["text"] = "before result" });
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.ChatCompletions, new() { AllowLoss = true }));
        Assert.Equal("tool_turn_order_not_representable", Assert.Single(error.Diagnostics).Code);
    }

    /// <summary>内嵌图片和文件引用仍绑定于原供应商，不能因位于工具结果而获得可移植性。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ProviderImageOrFileReference_RemainsNativeOnly(bool image)
    {
        var source = RichToolResultFixtures.Read(LlmProtocol.Responses, true); var parts = RichToolResultFixtures.Parts(source, LlmProtocol.Responses);
        parts[image ? 1 : 2] = CustomToolFixtures.Parse(image ? """{"type":"input_image","file_id":"image-id"}""" : """{"type":"input_file","file_id":"file-id"}""");
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, LlmProtocol.ClaudeMessages, new() { AllowLoss = true }));
        Assert.Equal("provider_file_reference", Assert.Single(error.Diagnostics).Code);
    }

    /// <summary>嵌套的 Claude 搜索引用仍属于加密回放状态，普通损耗许可不能删除它。</summary>
    [Fact]
    public void NestedEncryptedCitation_IsAlwaysNativeOnly()
    {
        var source = RichToolResultFixtures.Read(LlmProtocol.ClaudeMessages); RichToolResultFixtures.Parts(source, LlmProtocol.ClaudeMessages)[0]!["citations"] =
            new JsonArray(CustomToolFixtures.Parse("""{"type":"web_search_result_location","encrypted_index":"opaque","cited_text":"quoted","url":"https://source.invalid","title":"source"}"""));
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses, new() { AllowLoss = true }));
        Assert.Equal("opaque_state", Assert.Single(error.Diagnostics).Code);
        var native = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.ClaudeMessages);
        CustomToolConversionTests.Equal(source, CustomToolConversionTests.Read(native));
    }

    /// <summary>普通结果文本中的同名业务 JSON 不是原生引用位置，不应误判为加密状态。</summary>
    [Fact]
    public void UserJsonText_IsNotRecursivelyInspected()
    {
        var source = RichToolResultFixtures.Read(LlmProtocol.ClaudeMessages);
        RichToolResultFixtures.Parts(source, LlmProtocol.ClaudeMessages)[0]!["text"] = "{\"encrypted_index\":\"user data\"}";
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses);
        Assert.Empty(result.Diagnostics); Assert.Contains("encrypted_index", result.Data.GetRawText());
    }

    /// <summary>原生复制完整保留错误标记、富结果和未知扩展，不走有损转换器。</summary>
    [Fact]
    public void NativeErrorAndRichResult_AreUnchanged()
    {
        var source = RichToolResultFixtures.Read(LlmProtocol.ClaudeMessages, true); var result = RichToolResultFixtures.Result(source, LlmProtocol.ClaudeMessages);
        result["is_error"] = true; result["future"] = false;
        var copy = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.ClaudeMessages);
        Assert.Empty(copy.Diagnostics); CustomToolConversionTests.Equal(source, CustomToolConversionTests.Read(copy));
    }
}
