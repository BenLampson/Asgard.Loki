using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>完整引用转换必须保留文本范围、来源身份、工具关联和全部源数据。</summary>
public sealed class UrlCitationConversionTests
{
    /// <summary>两个独立完整目标快照，包括 Unicode、URL 查询/片段、标题、数组顺序和原始响应。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)]
    public void CompleteResponse_MatchesAuthoredGolden(LlmProtocol sourceProtocol)
    {
        var source = UrlCitationFixtures.Read(sourceProtocol.ToString()); var before = source.ToJsonString(); var target = UrlCitationFixtures.Other(sourceProtocol);
        var result = LlmProtocolConverter.ConvertResponse(source, sourceProtocol, target);
        CustomToolConversionTests.Equal(UrlCitationFixtures.Read("Expected" + target), CustomToolConversionTests.Read(result));
        Assert.Empty(result.Diagnostics); Assert.Equal(before, source.ToJsonString());
        CustomToolConversionTests.Equal(source, JsonNode.Parse(result.OriginalData.GetRawText()));
        var replay = LlmProtocolConverter.ConvertResponse(CustomToolConversionTests.Read(result), target, sourceProtocol);
        CustomToolConversionTests.Equal(UrlCitationFixtures.Annotations(source, sourceProtocol), UrlCitationFixtures.Annotations(CustomToolConversionTests.Read(replay), sourceProtocol));
    }

    /// <summary>ASCII 前缀在字符、UTF-16 和 UTF-8 计量中长度一致，可安全平移后续内容块引用。</summary>
    [Theory] [InlineData("", 0)] [InlineData("Lead ", 5)] [InlineData("Line\n", 5)]
    public void MultipleTextBlocks_RebaseOnlyKnownPrefix(string prefix, long offset)
    {
        var source = UrlCitationFixtures.Read("Responses"); var content = source["output"]![0]!["content"]!.AsArray();
        content.Insert(0, new JsonObject { ["type"] = "output_text", ["text"] = prefix, ["annotations"] = new JsonArray() });
        content.Add(new JsonObject { ["type"] = "output_text", ["text"] = " 后文😀", ["annotations"] = new JsonArray() });
        var result = LlmProtocolConverter.ConvertResponse(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions); var data = CustomToolConversionTests.Read(result);
        Assert.Empty(result.Diagnostics); Assert.Equal(prefix + "报告😀 see source. 后文😀", data["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        Assert.Equal(offset, UrlCitationFixtures.Citation(data, LlmProtocol.ChatCompletions)["start_index"]!.GetValue<long>());
        Assert.Equal(offset + 8, UrlCitationFixtures.Citation(data, LlmProtocol.ChatCompletions, 1)["end_index"]!.GetValue<long>());
    }

    /// <summary>非 ASCII 前缀单位不明时明确诊断，许可损耗也不能编造平移后的范围。</summary>
    [Theory] [InlineData("😀")] [InlineData("中")] [InlineData("e\u0301")]
    public void UnknownUnicodePrefix_RequiresSpecificConsent(string prefix)
    {
        var source = UrlCitationFixtures.Read("Responses"); source["output"]![0]!["content"]!.AsArray().Insert(0,
            new JsonObject { ["type"] = "output_text", ["text"] = prefix, ["annotations"] = new JsonArray() });
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions));
        Assert.Equal("citation_offset_unit_unknown", Assert.Single(error.Diagnostics).Code);
        var result = LlmProtocolConverter.ConvertResponse(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions, new() { AllowLoss = true });
        Assert.Equal("citation_offset_unit_unknown", Assert.Single(result.Diagnostics).Code);
        Assert.Null(CustomToolConversionTests.Read(result)["choices"]![0]!["message"]!["annotations"]);
        CustomToolConversionTests.Equal(source, JsonNode.Parse(result.OriginalData.GetRawText()));
    }

    /// <summary>不重排或按 URL 去重引用，同一来源可以指向不同文本范围。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)]
    public void DuplicateSources_KeepRangeAndOrder(LlmProtocol sourceProtocol)
    {
        var source = UrlCitationFixtures.Read(sourceProtocol.ToString()); var first = UrlCitationFixtures.Citation(source, sourceProtocol); var second = UrlCitationFixtures.Citation(source, sourceProtocol, 1);
        second["url"] = first["url"]!.DeepClone(); second["title"] = "";
        var target = UrlCitationFixtures.Other(sourceProtocol); var data = CustomToolConversionTests.Read(LlmProtocolConverter.ConvertResponse(source, sourceProtocol, target));
        Assert.Equal(2, UrlCitationFixtures.Annotations(data, target).Count);
        Assert.Equal(5L, UrlCitationFixtures.Citation(data, target, 1)["start_index"]!.GetValue<long>());
        Assert.Equal("", UrlCitationFixtures.Citation(data, target, 1)["title"]!.GetValue<string>());
    }

    /// <summary>文本未变时不猜测不同 Unicode 计量的长度，复制原始整数而非重新计算。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions, 2)] [InlineData(LlmProtocol.ChatCompletions, 3)] [InlineData(LlmProtocol.ChatCompletions, 7)]
    [InlineData(LlmProtocol.Responses, 2)] [InlineData(LlmProtocol.Responses, 3)] [InlineData(LlmProtocol.Responses, 7)]
    public void UnchangedUnicodeText_LeavesIndicesUninterpreted(LlmProtocol sourceProtocol, long end)
    {
        var source = UrlCitationFixtures.Read(sourceProtocol.ToString());
        if (sourceProtocol == LlmProtocol.Responses) source["output"]![0]!["content"]![0]!["text"] = "😀中";
        else source["choices"]![0]!["message"]!["content"] = "😀中";
        UrlCitationFixtures.Annotations(source, sourceProtocol).RemoveAt(1);
        var citation = UrlCitationFixtures.Citation(source, sourceProtocol); citation["start_index"] = 0; citation["end_index"] = end;
        var target = UrlCitationFixtures.Other(sourceProtocol); var result = LlmProtocolConverter.ConvertResponse(source, sourceProtocol, target);
        Assert.Empty(result.Diagnostics); Assert.Equal(end, UrlCitationFixtures.Citation(CustomToolConversionTests.Read(result), target)["end_index"]!.GetValue<long>());
    }

    /// <summary>引用范围不影响函数原始 ID、参数或工具终态，引用不会变成工具调用。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)]
    public void Citations_DoNotChangeFunctionAssociation(LlmProtocol sourceProtocol)
    {
        var source = ConversionFixtures.Response(sourceProtocol); var annotated = UrlCitationFixtures.Read(sourceProtocol.ToString());
        if (sourceProtocol == LlmProtocol.ChatCompletions)
        {
            source["choices"]![0]!["message"]!["content"] = "报告😀 see source.";
            source["choices"]![0]!["message"]!["annotations"] = UrlCitationFixtures.Annotations(annotated, sourceProtocol).DeepClone();
        }
        else source["output"]![0]!["content"] = annotated["output"]![0]!["content"]!.DeepClone();
        var target = UrlCitationFixtures.Other(sourceProtocol); var data = CustomToolConversionTests.Read(LlmProtocolConverter.ConvertResponse(source, sourceProtocol, target));
        var call = target == LlmProtocol.Responses ? data["output"]![1]! : data["choices"]![0]!["message"]!["tool_calls"]![0]!;
        Assert.Equal("call_1", call[target == LlmProtocol.Responses ? "call_id" : "id"]!.GetValue<string>());
        Assert.Equal("{\"x\":0}", (target == LlmProtocol.Responses ? call["arguments"] : call["function"]!["arguments"])!.GetValue<string>());
        Assert.Equal(2, UrlCitationFixtures.Annotations(data, target).Count);
    }

    /// <summary>空 annotations 可与工具专用的 null 文本并存，不要求不存在的引用文本。</summary>
    [Fact]
    public void EmptyAnnotations_DoNotRequireText()
    {
        var source = ConversionFixtures.Response(LlmProtocol.ChatCompletions); source["choices"]![0]!["message"]!["content"] = null;
        source["choices"]![0]!["message"]!["annotations"] = new JsonArray();
        var result = LlmProtocolConverter.ConvertResponse(source, LlmProtocol.ChatCompletions, LlmProtocol.Responses);
        Assert.Empty(result.Diagnostics); Assert.Equal("function_call", CustomToolConversionTests.Read(result)["output"]![0]!["type"]!.GetValue<string>());
    }

    /// <summary>搜索执行记录仍独立诊断，允许省略它不会删除已经可表达的引用。</summary>
    [Fact]
    public void SearchExecution_RemainsIndependentFromDisplayCitations()
    {
        var source = UrlCitationFixtures.Read("Responses"); source["output"]!.AsArray().Insert(0,
            CustomToolFixtures.Parse("""{"type":"web_search_call","id":"search1","status":"completed","action":{"type":"search","query":"q"}}"""));
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions));
        var result = LlmProtocolConverter.ConvertResponse(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions, new() { AllowLoss = true });
        Assert.Contains(result.Diagnostics, d => d.Code == "hosted_search_execution_not_representable");
        Assert.Equal(2, UrlCitationFixtures.Annotations(CustomToolConversionTests.Read(result), LlmProtocol.ChatCompletions).Count);
        Assert.DoesNotContain("function_call", result.Data.GetRawText()); Assert.Contains("web_search_call", result.OriginalData.GetRawText());
    }
}
