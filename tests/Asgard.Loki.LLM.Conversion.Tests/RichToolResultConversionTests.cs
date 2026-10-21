using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>富工具结果的内容种类、原始调用身份和数组顺序不能在转换中消失。</summary>
public sealed class RichToolResultConversionTests
{
    /// <summary>六个文本数组方向和两个媒体数组方向。</summary>
    public static TheoryData<LlmProtocol, LlmProtocol, bool> Directions => RichToolResultFixtures.Directions();

    /// <summary>完整请求金样及往返保持原始 ID、数组文本边界、空文本和二进制媒体。</summary>
    [Theory] [MemberData(nameof(Directions))]
    public void CompleteRequest_MatchesIndependentGolden(LlmProtocol sourceProtocol, LlmProtocol target, bool media)
    {
        var source = RichToolResultFixtures.Read(sourceProtocol, media); var before = source.ToJsonString();
        var result = LlmProtocolConverter.ConvertRequest(source, sourceProtocol, target); var data = CustomToolConversionTests.Read(result);
        CustomToolConversionTests.Equal(RichToolResultFixtures.Read(target, media, true), data); Assert.Empty(result.Diagnostics);
        Assert.Equal(before, source.ToJsonString()); CustomToolConversionTests.Equal(source, JsonNode.Parse(result.OriginalData.GetRawText()));
        var replay = LlmProtocolConverter.ConvertRequest(data, target, sourceProtocol);
        CustomToolConversionTests.Equal(RichToolResultFixtures.Read(sourceProtocol, media, true), CustomToolConversionTests.Read(replay));
    }

    /// <summary>空结果数组保持为空数组，不变成空字符串或遗漏结果项。</summary>
    [Theory] [MemberData(nameof(Directions))]
    public void EmptyArray_RemainsAnArray(LlmProtocol sourceProtocol, LlmProtocol target, bool media)
    {
        var source = RichToolResultFixtures.Read(sourceProtocol, media); RichToolResultFixtures.SetContent(source, sourceProtocol, new JsonArray());
        var result = LlmProtocolConverter.ConvertRequest(source, sourceProtocol, target); Assert.Empty(result.Diagnostics);
        Assert.Empty(RichToolResultFixtures.Parts(CustomToolConversionTests.Read(result), target));
    }

    /// <summary>明确的 false 与原生默认一致；未标记错误的数据仍保持原数组而不改写提示文本。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)]
    public void ExplicitNonError_PreservesResultContent(LlmProtocol target)
    {
        var source = RichToolResultFixtures.Read(LlmProtocol.ClaudeMessages); RichToolResultFixtures.Result(source, LlmProtocol.ClaudeMessages)["is_error"] = false;
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, target);
        Assert.Empty(result.Diagnostics); CustomToolConversionTests.Equal(RichToolResultFixtures.Read(target, golden: true), CustomToolConversionTests.Read(result));
        Assert.Contains("is_error", result.OriginalData.GetRawText());
    }

    /// <summary>并行同名调用的逆序结果只按原始 ID 配对，Chat 的工具轮次必须集中声明。</summary>
    [Fact]
    public void ParallelSameNameResults_KeepIdsAndOneChatToolTurn()
    {
        var source = RichToolResultFixtures.Read(LlmProtocol.Responses); var input = source["input"]!.AsArray();
        var nextCall = input[1]!.DeepClone(); nextCall["call_id"] = "call_second"; nextCall["arguments"] = "{\"n\":1}";
        var nextResult = input[2]!.DeepClone(); nextResult["call_id"] = "call_second"; nextResult["output"] = CustomToolFixtures.Parse("""{"parts":[{"type":"input_text","text":"second"}]}""")["parts"]!.DeepClone();
        input.Insert(2, nextCall); input.Insert(3, nextResult);
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions); var messages = CustomToolConversionTests.Read(result)["messages"]!.AsArray();
        Assert.Equal(4, messages.Count); var calls = messages[1]!["tool_calls"]!.AsArray(); Assert.Equal(2, calls.Count);
        Assert.Equal("call_rich", calls[0]!["id"]!.GetValue<string>()); Assert.Equal("call_second", calls[1]!["id"]!.GetValue<string>());
        Assert.Equal("call_second", messages[2]!["tool_call_id"]!.GetValue<string>()); Assert.Equal("second", messages[2]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("call_rich", messages[3]!["tool_call_id"]!.GetValue<string>()); Assert.Equal(3, messages[3]!["content"]!.AsArray().Count);
    }

    /// <summary>所有结果回填后才出现的普通用户文本仍然可表达，不能因同一个源消息而误拒绝。</summary>
    [Fact]
    public void TextAfterAllResults_RemainsASeparateValidUserMessage()
    {
        var source = RichToolResultFixtures.Read(LlmProtocol.ClaudeMessages); source["messages"]![2]!["content"]!.AsArray().Add(new JsonObject { ["type"] = "text", ["text"] = "after results" });
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.ChatCompletions);
        var messages = CustomToolConversionTests.Read(result)["messages"]!.AsArray(); Assert.Empty(result.Diagnostics); Assert.Equal(4, messages.Count);
        Assert.Equal("tool", messages[2]!["role"]!.GetValue<string>()); Assert.Equal("call_rich", messages[2]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("user", messages[3]!["role"]!.GetValue<string>()); Assert.Equal("after results", messages[3]!["content"]![0]!["text"]!.GetValue<string>());
    }
}
