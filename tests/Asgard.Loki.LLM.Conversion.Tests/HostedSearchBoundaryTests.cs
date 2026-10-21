using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>区分引用损耗、原生加密搜索状态与普通用户 JSON，保证旧的损耗许可不能绕过回放边界。</summary>
public sealed class HostedSearchBoundaryTests
{
    /// <summary>完整响应中的加密搜索内容或引用索引即使显式允许普通损耗也必须失败。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void EncryptedResponse_IsAlwaysNativeOnly(bool citation)
    {
        var source = EncryptedResponse(citation); var before = source.ToJsonString();
        foreach (var target in Targets)
        {
            var exception = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(source, LlmProtocol.ClaudeMessages, target, new() { AllowLoss = true }));
            Assert.Equal("opaque_state", Assert.Single(exception.Diagnostics).Code);
        }
        Assert.Equal(before, source.ToJsonString());
    }

    /// <summary>带加密搜索结果的消息历史也不能被默默删成普通文本请求。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void EncryptedHistory_IsAlwaysNativeOnly(bool citation)
    {
        var request = HostedSearchFixtures.Request(LlmProtocol.ClaudeMessages); request["messages"]!.AsArray().Add(new JsonObject { ["role"] = "assistant", ["content"] = EncryptedResponse(citation)["content"]!.DeepClone() });
        foreach (var target in Targets)
            Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.ClaudeMessages, target, new() { AllowLoss = true }));
    }

    /// <summary>先返回文本后遇到加密结果/引用增量，立即终止转换且不能产生成功终态。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void EncryptedStream_FailsAfterEarlierDeltas(bool citation)
    {
        foreach (var target in Targets)
        {
            var converter = new LlmProtocolStreamConverter(LlmProtocol.ClaudeMessages, target, new() { AllowLoss = true });
            var source = HostedSearchFixtures.TextStream(LlmProtocol.ClaudeMessages); var output = new List<LlmConversionResult>();
            foreach (var e in source.Take(citation ? 3 : 4)) output.AddRange(converter.Push(e));
            var bad = citation ? CustomToolFixtures.Parse("""{"type":"content_block_delta","index":0,"delta":{"type":"citations_delta","citation":{"type":"web_search_result_location","url":"https://source.invalid","title":"source","encrypted_index":"opaque","cited_text":"quoted"}}}""")
                : new JsonObject { ["type"] = "content_block_start", ["index"] = 1, ["content_block"] = HostedSearchFixtures.EncryptedResult() };
            var error = Assert.Throws<LlmConversionException>(() => converter.Push(bad)); Assert.Equal("opaque_state", Assert.Single(error.Diagnostics).Code);
            Assert.NotEmpty(output); Assert.Throws<InvalidOperationException>(() => converter.Complete());
            Assert.DoesNotContain(output, e => e.Data.GetRawText().Contains("response.completed", StringComparison.Ordinal) || e.Data.GetRawText().Contains("message_stop", StringComparison.Ordinal));
        }
    }

    /// <summary>同协议复制仍保留完整加密搜索状态，并不因为选择转换包 API 而删除数据。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void NativeIdentity_PreservesEncryptedState(bool citation)
    {
        var source = EncryptedResponse(citation); var result = LlmProtocolConverter.ConvertResponse(source, LlmProtocol.ClaudeMessages, LlmProtocol.ClaudeMessages);
        CustomToolConversionTests.Equal(source, JsonNode.Parse(result.Data.GetRawText()));
        var converter = new LlmProtocolStreamConverter(LlmProtocol.ClaudeMessages, LlmProtocol.ClaudeMessages);
        var e = new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = HostedSearchFixtures.EncryptedResult() };
        CustomToolConversionTests.Equal(e, CustomToolConversionTests.Read(Assert.Single(converter.Push(e))));
    }

    /// <summary>已知原生回放类型即使省略或置空 token，也不能经损耗许可被当成普通显示注解。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void OpaqueType_IsNotMadePortableByMissingToken(bool missing)
    {
        var source = EncryptedResponse(true); var citation = source["content"]![0]!["citations"]![0]!.AsObject();
        if (missing) citation.Remove("encrypted_index"); else citation["encrypted_index"] = null;
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses, new() { AllowLoss = true }));
    }

    /// <summary>合法引用/grounding 无法表达时保留专门诊断；只有明确的普通损耗许可允许省略。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.Gemini)]
    public void CitationAndGroundingLoss_HasSpecificDiagnostics(LlmProtocol sourceProtocol)
    {
        var source = HostedSearchFixtures.TextResponse(sourceProtocol); var citation = CustomToolFixtures.Parse("""{"type":"url_citation","url":"https://source.invalid","title":"source","start_index":0,"end_index":4}""");
        if (sourceProtocol == LlmProtocol.Responses) source["output"]![0]!["content"]![0]!["annotations"]!.AsArray().Add(citation);
        else if (sourceProtocol == LlmProtocol.ChatCompletions)
        {
            citation.Remove("type"); source["choices"]![0]!["message"]!["annotations"] = new JsonArray(new JsonObject { ["type"] = "url_citation", ["url_citation"] = citation });
        }
        else source["candidates"]![0]!["groundingMetadata"] = CustomToolFixtures.Parse("""{"webSearchQueries":["q"],"groundingChunks":[{"web":{"uri":"https://source.invalid","title":"source"}}]}""");
        var target = sourceProtocol == LlmProtocol.Gemini ? LlmProtocol.Responses : LlmProtocol.ClaudeMessages;
        var code = sourceProtocol == LlmProtocol.Gemini ? "grounding_metadata_not_representable" : "search_citations_not_representable";
        var failure = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(source, sourceProtocol, target)); Assert.Contains(failure.Diagnostics, d => d.Code == code);
        var result = LlmProtocolConverter.ConvertResponse(source, sourceProtocol, target, new() { AllowLoss = true }); Assert.Contains(result.Diagnostics, d => d.Code == code);
        CustomToolConversionTests.Equal(source, JsonNode.Parse(result.OriginalData.GetRawText())); Assert.DoesNotContain("source.invalid", result.Data.GetRawText());
    }

    /// <summary>搜索服务的调用和错误不是成功的用户函数调用，不能把资源上限错误改写成成功搜索。</summary>
    [Fact]
    public void SearchError_RemainsVisibleInOriginalAndDiagnostics()
    {
        var source = HostedSearchFixtures.TextResponse(LlmProtocol.ClaudeMessages);
        source["content"]!.AsArray().Insert(0, CustomToolFixtures.Parse("""{"type":"web_search_tool_result","tool_use_id":"search1","content":{"type":"web_search_tool_result_error","error_code":"max_uses_exceeded"}}"""));
        source["content"]!.AsArray().Insert(0, CustomToolFixtures.Parse("""{"type":"server_tool_use","id":"search1","name":"web_search","input":{"query":"q"}}"""));
        var result = LlmProtocolConverter.ConvertResponse(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses, new() { AllowLoss = true });
        Assert.Contains(result.Diagnostics, d => d.Code == "hosted_search_error_not_representable"); Assert.Contains(result.Diagnostics, d => d.Code == "hosted_search_execution_not_representable");
        Assert.Contains("max_uses_exceeded", result.OriginalData.GetRawText()); Assert.DoesNotContain("function_call", result.Data.GetRawText());
    }

    /// <summary>具有同名键的普通函数参数是用户业务 JSON，不得被全对象递归扫描误当成加密回放状态。</summary>
    [Fact]
    public void UserJson_IsNotOpaqueProtocolState()
    {
        var source = ConversionFixtures.Request(LlmProtocol.ClaudeMessages);
        source["messages"]![1]!["content"]![0]!["input"] = CustomToolFixtures.Parse("""{"encrypted_content":"user data","encrypted_index":"user data"}""");
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses);
        Assert.Contains("encrypted_content", result.Data.GetRawText()); Assert.Empty(result.Diagnostics);
    }

    /// <summary>构造真实协议位置的不可跨协议回放字段。</summary>
    internal static JsonObject EncryptedResponse(bool citation)
    {
        var response = HostedSearchFixtures.TextResponse(LlmProtocol.ClaudeMessages);
        if (citation) response["content"]![0]!["citations"] = new JsonArray(CustomToolFixtures.Parse("""{"type":"web_search_result_location","url":"https://source.invalid","title":"source","encrypted_index":"opaque","cited_text":"quoted"}"""));
        else response["content"]!.AsArray().Insert(0, HostedSearchFixtures.EncryptedResult());
        return response;
    }
    private static LlmProtocol[] Targets => [LlmProtocol.ChatCompletions, LlmProtocol.Responses, LlmProtocol.Gemini];
}
