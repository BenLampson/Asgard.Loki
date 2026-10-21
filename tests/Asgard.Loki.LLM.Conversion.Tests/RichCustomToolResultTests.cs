using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>custom 结果数组复用原请求身份表，不能与普通函数或未声明的 Gemini 封装混淆。</summary>
public sealed class RichCustomToolResultTests
{
    /// <summary>文本可转 Chat/Claude，含媒体的 custom 结果只在已验证的 Claude 容器中表示。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions, false)] [InlineData(LlmProtocol.ClaudeMessages, false)] [InlineData(LlmProtocol.ClaudeMessages, true)]
    public void CustomArray_UsesSameCallIdAndAuthoredNativeShape(LlmProtocol target, bool media)
    {
        var source = Request(media); var before = source.ToJsonString(); var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, target, CustomToolFixtures.Options);
        var data = CustomToolConversionTests.Read(result); var alias = CustomToolFixtures.Name(data, target);
        CustomToolConversionTests.Equal(Expected(target, alias, media), data); Assert.Empty(result.Diagnostics);
        Assert.Equal(1, result.Context!.CustomToolCount); Assert.Equal(before, source.ToJsonString());
        CustomToolConversionTests.Equal(source, JsonNode.Parse(result.OriginalData.GetRawText()));
    }

    /// <summary>空 custom 数组现在是合法的已支持容器，不会作为字符串或孤儿结果处理。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)]
    public void EmptyCustomArray_KeepsItsNativeKind(LlmProtocol target)
    {
        var source = Request(); source["input"]![2]!["output"] = new JsonArray();
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, target, CustomToolFixtures.Options);
        Assert.Empty(result.Diagnostics); Assert.Empty(RichToolResultFixtures.Parts(CustomToolConversionTests.Read(result), target));
    }

    /// <summary>现有字节限制扩展到数组的完整 JSON，不能把大文本/媒体塞进数组绕过上限。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)]
    public void CustomArray_RespectsExactJsonByteLimit(LlmProtocol target)
    {
        var source = Request(); var bytes = Encoding.UTF8.GetByteCount(source["input"]![2]!["output"]!.ToJsonString());
        var fit = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, target, CustomToolFixtures.Options with { MaxCustomToolInputBytes = bytes });
        Assert.Empty(fit.Diagnostics);
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, target,
            CustomToolFixtures.Options with { MaxCustomToolInputBytes = bytes - 1, AllowLoss = true }));
        Assert.Equal("custom_tool_input_limit", Assert.Single(error.Diagnostics).Code);
    }

    /// <summary>空数组的两个 JSON 字节也受限，大小预检不能把合法边界按估算上界误拒绝。</summary>
    [Fact]
    public void EmptyCustomArray_RespectsTwoByteBoundary()
    {
        var source = Request(); source["input"]![1]!["input"] = ""; source["input"]![2]!["output"] = new JsonArray();
        var fit = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions, CustomToolFixtures.Options with { MaxCustomToolInputBytes = 2 });
        Assert.Empty(fit.Diagnostics);
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions, CustomToolFixtures.Options with { MaxCustomToolInputBytes = 1 }));
    }

    /// <summary>原始数组文本中的无效 UTF-16 不能先经 JSON 序列化替换为另一个有效字符。</summary>
    [Fact]
    public void InvalidUnicodeInCustomArray_IsNotReplaced()
    {
        var source = Request(); source["input"]![2]!["output"]![0]!["text"] = "\ud800";
        Assert.Throws<EncoderFallbackException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions, CustomToolFixtures.Options));
    }

    /// <summary>缺少相同容器时，普通损耗许可仍不能创造 Gemini 包装或把 Chat 媒体压成文本。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions, true)] [InlineData(LlmProtocol.Gemini, false)] [InlineData(LlmProtocol.Gemini, true)]
    public void UnrepresentableCustomArray_IsRejected(LlmProtocol target, bool media)
    {
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(Request(media), LlmProtocol.Responses, target, CustomToolFixtures.Options with { AllowLoss = true }));
        Assert.Equal(target == LlmProtocol.Gemini ? "custom_tool_result_content_not_supported" : "rich_tool_result_not_representable", Assert.Single(error.Diagnostics).Code);
    }

    /// <summary>namespace 普通函数的结果数组保持原 call_id，不能按工具显示名称误配。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)]
    public void NamespacedFunctionArray_UsesRequestLocalIdentity(LlmProtocol target)
    {
        var source = RichToolResultFixtures.Read(LlmProtocol.Responses); var function = source["tools"]![0]!.DeepClone();
        source["tools"] = new JsonArray(new JsonObject { ["type"] = "namespace", ["name"] = "docs", ["description"] = "", ["tools"] = new JsonArray(function) });
        source["input"]![1]!["namespace"] = "docs"; source["input"]![2]!["namespace"] = "docs"; source["input"]![2]!["name"] = "lookup";
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, target, new() { EnableNamespacedToolConversion = true });
        var data = CustomToolConversionTests.Read(result); Assert.Empty(result.Diagnostics); Assert.Equal(1, result.Context!.NamespacedToolCount);
        CustomToolConversionTests.Equal(RichToolResultFixtures.Parts(RichToolResultFixtures.Read(target, golden: true), target), RichToolResultFixtures.Parts(data, target));
        Assert.Equal("call_rich", RichToolResultFixtures.Result(data, target)[target == LlmProtocol.ChatCompletions ? "tool_call_id" : "tool_use_id"]!.GetValue<string>());
    }

    /// <summary>原生 custom 调用与结果数组，定义、强制选择和原始 ID 独立于生成别名。</summary>
    internal static JsonObject Request(bool media = false)
    {
        var request = CustomToolFixtures.Request(); request["input"] = new JsonArray(
            CustomToolFixtures.Parse("""{"type":"message","role":"user","content":[{"type":"input_text","text":"start"}]}"""),
            CustomToolFixtures.Parse("""{"type":"custom_tool_call","call_id":"call_rich","name":"code","input":"free text"}"""),
            new JsonObject { ["type"] = "custom_tool_call_output", ["call_id"] = "call_rich", ["output"] = RichToolResultFixtures.Parts(RichToolResultFixtures.Read(LlmProtocol.Responses, media), LlmProtocol.Responses).DeepClone() });
        return request;
    }

    /// <summary>独立工具定义金样与内容金样组合，只绑定本次实际别名，不调用生产转换器。</summary>
    internal static JsonObject Expected(LlmProtocol target, string alias, bool media)
    {
        var expected = CustomToolFixtures.Bind(CustomToolFixtures.ExpectedRequest(target), alias);
        expected["messages"] = RichToolResultFixtures.Read(target, media, true)["messages"]!.DeepClone();
        if (target == LlmProtocol.ChatCompletions)
        {
            var call = expected["messages"]![1]!["tool_calls"]![0]!["function"]!; call["name"] = alias; call["arguments"] = "{\"input\":\"free text\"}";
        }
        else { var call = expected["messages"]![1]!["content"]![0]!; call["name"] = alias; call["input"] = CustomToolFixtures.Parse("""{"input":"free text"}"""); }
        return expected;
    }
}
