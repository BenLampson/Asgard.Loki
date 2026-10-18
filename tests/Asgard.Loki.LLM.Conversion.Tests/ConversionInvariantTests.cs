using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>固定不透明状态、终态、数据损坏和字段顺序的回归边界。</summary>
public sealed class ConversionInvariantTests
{
    /// <summary>显式允许普通损耗也不能丢弃签名或加密推理。</summary>
    [Theory]
    [InlineData(LlmProtocol.ClaudeMessages)]
    [InlineData(LlmProtocol.Gemini)]
    [InlineData(LlmProtocol.Responses)]
    public void OpaqueState_AlwaysRequiresNativeProtocol(LlmProtocol source)
    {
        var request = ConversionFixtures.Request(source);
        if (source == LlmProtocol.ClaudeMessages) request["messages"]![1]!["content"]!.AsArray().Insert(0, new JsonObject { ["type"] = "thinking", ["thinking"] = "summary", ["signature"] = "opaque" });
        else if (source == LlmProtocol.Gemini) request["contents"]![1]!["parts"]![0]!["thoughtSignature"] = "opaque";
        else request["input"]!.AsArray().Insert(0, new JsonObject { ["type"] = "reasoning", ["encrypted_content"] = "opaque" });
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, source, LlmProtocol.ChatCompletions, new() { AllowLoss = true }));
        Assert.Contains(error.Diagnostics, d => d.Code == "opaque_state");
    }

    /// <summary>失败后的流转换器不能继续追加或生成成功终态。</summary>
    [Fact]
    public void FailedStream_CannotBeFinalizedSuccessfully()
    {
        var converter = new LlmProtocolStreamConverter(LlmProtocol.ClaudeMessages, LlmProtocol.Responses);
        Assert.Throws<LlmConversionException>(() => converter.Push(new() { ["type"] = "error", ["error"] = new JsonObject() }));
        Assert.Throws<InvalidOperationException>(() => converter.Complete());
        Assert.Throws<InvalidOperationException>(() => converter.Push(new() { ["type"] = "message_stop" }));
    }

    /// <summary>不能把合法但未完成的函数参数补成 JSON 或执行两次。</summary>
    [Fact]
    public void TruncatedToolArguments_AreNeverRepaired()
    {
        var converter = new LlmProtocolStreamConverter(LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages);
        converter.Push(JsonNode.Parse("""{"id":"r1","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"f","arguments":"{\"x\":"}}]},"finish_reason":"length"}]}""")!.AsObject());
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => converter.Complete());
    }

    /// <summary>最终 Responses 输出必须与已发出的增量一致。</summary>
    [Fact]
    public void ResponsesFinalSnapshot_MustAgreeWithDeltas()
    {
        var converter = new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.ChatCompletions);
        var events = ConversionFixtures.Stream(LlmProtocol.Responses).ToArray();
        foreach (var item in events[..^1]) converter.Push(item);
        events[^1]["response"]!["output"]![0]!["content"]![0]!["text"] = "different";
        Assert.Throws<InvalidDataException>(() => converter.Push(events[^1]));
    }

    /// <summary>具名选择以外的 none 不会通过删除函数定义掩盖上游问题。</summary>
    [Theory]
    [InlineData(LlmProtocol.Responses)]
    [InlineData(LlmProtocol.ClaudeMessages)]
    [InlineData(LlmProtocol.Gemini)]
    public void ToolChoiceNone_PreservesFunctionDefinitions(LlmProtocol target)
    {
        var request = ConversionFixtures.Request(LlmProtocol.ChatCompletions); request["tool_choice"] = "none";
        var result = LlmProtocolConverter.ConvertRequest(request, LlmProtocol.ChatCompletions, target);
        var body = JsonNode.Parse(result.Data.GetRawText())!;
        Assert.NotEmpty(body["tools"]!.AsArray());
        var choice = target == LlmProtocol.Gemini ? body["toolConfig"]!["functionCallingConfig"]!["mode"]!.GetValue<string>()
            : target == LlmProtocol.ClaudeMessages ? body["tool_choice"]!["type"]!.GetValue<string>() : body["tool_choice"]!.GetValue<string>();
        Assert.Equal(target == LlmProtocol.Gemini ? "NONE" : "none", choice);
    }

    /// <summary>未知 usage 字段必须被诊断，允许损耗时仍保留上游 JSON。</summary>
    [Fact]
    public void UsageDetails_AreNotSilentlyDiscarded()
    {
        var response = ConversionFixtures.Response(LlmProtocol.ClaudeMessages); response["usage"]!["cache_creation_input_tokens"] = 7;
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(response, LlmProtocol.ClaudeMessages, LlmProtocol.ChatCompletions));
        var result = LlmProtocolConverter.ConvertResponse(response, LlmProtocol.ClaudeMessages, LlmProtocol.ChatCompletions, new() { AllowLoss = true });
        Assert.Equal(7, result.OriginalData.GetProperty("usage").GetProperty("cache_creation_input_tokens").GetInt32());
        Assert.Contains(result.Diagnostics, d => d.Path == "$usage.cache_creation_input_tokens");
    }

    /// <summary>并行函数参数交错时保持各自的索引、ID、名称和 JSON。</summary>
    [Fact]
    public void InterleavedToolArguments_PreserveSeparateCalls()
    {
        var converter = new LlmProtocolStreamConverter(LlmProtocol.ChatCompletions, LlmProtocol.Responses);
        converter.Push(JsonNode.Parse("""{"id":"r1","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_a","function":{"name":"first","arguments":"{\"a\":"}},{"index":1,"id":"call_b","function":{"name":"second","arguments":"{\"b\":"}}]}}]}""")!.AsObject());
        converter.Push(JsonNode.Parse("""{"id":"r1","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":"2}"}},{"index":0,"function":{"arguments":"1}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}""")!.AsObject());
        var output = converter.Complete().Last().Data.GetProperty("response").GetProperty("output");
        Assert.Equal("call_a", output[0].GetProperty("call_id").GetString()); Assert.Equal("{\"a\":1}", output[0].GetProperty("arguments").GetString());
        Assert.Equal("call_b", output[1].GetProperty("call_id").GetString()); Assert.Equal("{\"b\":2}", output[1].GetProperty("arguments").GetString());
    }

    /// <summary>缺失 Gemini 工具 ID 时，只能在名称唯一时关联回填。</summary>
    [Fact]
    public void GeminiMissingIds_RejectAmbiguousFunctionResults()
    {
        var request = ConversionFixtures.Request(LlmProtocol.Gemini);
        var call = request["contents"]![1]!["parts"]![0]!["functionCall"]!.AsObject(); call.Remove("id");
        request["contents"]![1]!["parts"]!.AsArray().Add(new JsonObject { ["functionCall"] = call.DeepClone() });
        request["contents"]![2]!["parts"]![0]!["functionResponse"]!.AsObject().Remove("id");
        Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.Gemini, LlmProtocol.ChatCompletions));
    }

    /// <summary>来自 OpenAI 协议的真实创建时间沿正确字段映射。</summary>
    [Fact]
    public void CreationTimestamp_IsMappedWithoutCurrentTimeDependency()
    {
        var response = ConversionFixtures.Response(LlmProtocol.ChatCompletions); response["created"] = 1234567890;
        var converted = LlmProtocolConverter.ConvertResponse(response, LlmProtocol.ChatCompletions, LlmProtocol.Responses);
        Assert.Equal(1234567890, converted.Data.GetProperty("created_at").GetInt64());
    }

    /// <summary>正常 Responses 显式 error:null 不能被误判成上游失败。</summary>
    [Fact]
    public void ResponsesNullError_IsNotFailure()
    {
        var response = ConversionFixtures.Response(LlmProtocol.Responses); response["error"] = null;
        var result = LlmProtocolConverter.ConvertResponse(response, LlmProtocol.Responses, LlmProtocol.ChatCompletions);
        Assert.Empty(result.Diagnostics); Assert.Equal("r1", result.Data.GetProperty("id").GetString());
    }

    /// <summary>指令从对话中间移动到顶层会改变语义，默认拒绝。</summary>
    [Fact]
    public void LateSystemInstruction_RequiresExplicitLossConsent()
    {
        var request = ConversionFixtures.Request(LlmProtocol.ChatCompletions);
        request["messages"]!.AsArray().Add(new JsonObject { ["role"] = "system", ["content"] = "later" });
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages));
        Assert.Contains(error.Diagnostics, d => d.Code == "instruction_order_changed");
    }

    /// <summary>不完整响应的原因保持真实语义，不能把内容阻断误写为 token 截断。</summary>
    [Theory]
    [InlineData("max_output_tokens", "length")]
    [InlineData("content_filter", "content_filter")]
    public void IncompleteResponses_PreserveFinishReason(string sourceReason, string expected)
    {
        var response = ConversionFixtures.Response(LlmProtocol.Responses); response["status"] = "incomplete";
        response["incomplete_details"] = new JsonObject { ["reason"] = sourceReason };
        var converted = LlmProtocolConverter.ConvertResponse(response, LlmProtocol.Responses, LlmProtocol.ChatCompletions);
        Assert.Equal(expected, converted.Data.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    /// <summary>无法识别的终止原因即使允许普通字段损耗也不能猜测。</summary>
    [Fact]
    public void UnknownIncompleteReason_IsNeverInvented()
    {
        var response = ConversionFixtures.Response(LlmProtocol.Responses); response["status"] = "incomplete";
        response["incomplete_details"] = new JsonObject { ["reason"] = "future" };
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(response, LlmProtocol.Responses, LlmProtocol.ChatCompletions, new() { AllowLoss = true }));
    }
}
