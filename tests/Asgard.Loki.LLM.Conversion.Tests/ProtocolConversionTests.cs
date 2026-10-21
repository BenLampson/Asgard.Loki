using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>独立断言十二条方向的请求、响应与流式语义，不仅测试可序列化。</summary>
public sealed class ProtocolConversionTests
{
    /// <summary>四种协议之间的十二条显式转换路径。</summary>
    public static TheoryData<LlmProtocol, LlmProtocol> Routes
    {
        get
        {
            TheoryData<LlmProtocol, LlmProtocol> routes = [];
            foreach (var from in Protocols)
            foreach (var to in Protocols)
                if (from != to) routes.Add(from, to);
            return routes;
        }
    }
    private static LlmProtocol[] Protocols => [LlmProtocol.ChatCompletions, LlmProtocol.Responses, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini];

    /// <summary>核对最终字段路径、工具定义、参数、图片与结果关联，源请求保持不变。</summary>
    [Theory]
    [MemberData(nameof(Routes))]
    public void RequestMatrix_MapsNativeWireFields(LlmProtocol from, LlmProtocol to)
    {
        var source = ConversionFixtures.Request(from); var before = source.ToJsonString();
        var result = LlmProtocolConverter.ConvertRequest(source, from, to);
        var target = JsonNode.Parse(result.Data.GetRawText())!;
        Assert.Empty(result.Diagnostics); Assert.Equal(before, source.ToJsonString());
        Assert.True(JsonNode.DeepEquals(source, JsonNode.Parse(result.OriginalData.GetRawText())));
        Assert.Equal("model-a", target["model"]!.GetValue<string>());
        var config = to == LlmProtocol.Gemini ? target["generationConfig"]! : target;
        Assert.Equal(0, config["temperature"]!.GetValue<int>());
        Assert.Equal(128, config[to switch { LlmProtocol.Gemini => "maxOutputTokens", LlmProtocol.Responses => "max_output_tokens", LlmProtocol.ClaudeMessages => "max_tokens", _ => "max_completion_tokens" }]!.GetValue<int>());
        switch (to)
        {
            case LlmProtocol.ChatCompletions:
                Assert.Equal("rules", target["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>());
                Assert.Equal("data:image/png;base64,aGVsbG8=", target["messages"]![1]!["content"]![1]!["image_url"]!["url"]!.GetValue<string>());
                Assert.Equal("call_1", target["messages"]![2]!["tool_calls"]![0]!["id"]!.GetValue<string>());
                Assert.Equal("call_1", target["messages"]![3]!["tool_call_id"]!.GetValue<string>());
                Assert.Equal("{\"ok\":false}", target["messages"]![3]!["content"]!.GetValue<string>());
                Assert.Equal("lookup", target["tools"]![0]!["function"]!["name"]!.GetValue<string>());
                Assert.Equal("lookup", target["tool_choice"]!["function"]!["name"]!.GetValue<string>());
                break;
            case LlmProtocol.Responses:
                Assert.Equal("rules", target["input"]![0]!["content"]![0]!["text"]!.GetValue<string>());
                Assert.Equal("input_image", target["input"]![1]!["content"]![1]!["type"]!.GetValue<string>());
                Assert.Equal("call_1", target["input"]![2]!["call_id"]!.GetValue<string>());
                Assert.Equal("function_call_output", target["input"]![3]!["type"]!.GetValue<string>());
                Assert.Equal("{\"ok\":false}", target["input"]![3]!["output"]!.GetValue<string>());
                Assert.Equal("lookup", target["tool_choice"]!["name"]!.GetValue<string>());
                break;
            case LlmProtocol.ClaudeMessages:
                Assert.Equal("rules", target["system"]![0]!["text"]!.GetValue<string>());
                Assert.Equal("aGVsbG8=", target["messages"]![0]!["content"]![1]!["source"]!["data"]!.GetValue<string>());
                Assert.Equal("call_1", target["messages"]![1]!["content"]![0]!["id"]!.GetValue<string>());
                Assert.Equal(0, target["messages"]![1]!["content"]![0]!["input"]!["x"]!.GetValue<int>());
                Assert.Equal("call_1", target["messages"]![2]!["content"]![0]!["tool_use_id"]!.GetValue<string>());
                Assert.Equal("tool", target["tool_choice"]!["type"]!.GetValue<string>());
                break;
            case LlmProtocol.Gemini:
                Assert.Equal("rules", target["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
                Assert.Equal("image/png", target["contents"]![0]!["parts"]![1]!["inlineData"]!["mimeType"]!.GetValue<string>());
                Assert.Equal("call_1", target["contents"]![1]!["parts"]![0]!["functionCall"]!["id"]!.GetValue<string>());
                Assert.False(target["contents"]![2]!["parts"]![0]!["functionResponse"]!["response"]!["ok"]!.GetValue<bool>());
                Assert.Equal("lookup", target["tools"]![0]!["functionDeclarations"]![0]!["name"]!.GetValue<string>());
                Assert.Equal("lookup", target["toolConfig"]!["functionCallingConfig"]!["allowedFunctionNames"]![0]!.GetValue<string>());
                break;
        }
    }

    /// <summary>响应转换必须保留真实文本、函数 ID、参数及 token 用量。</summary>
    [Theory]
    [MemberData(nameof(Routes))]
    public void ResponseMatrix_PreservesContentCallsAndUsage(LlmProtocol from, LlmProtocol to)
    {
        var source = ConversionFixtures.Response(from); var before = source.ToJsonString();
        var converted = LlmProtocolConverter.ConvertResponse(source, from, to);
        Assert.Empty(converted.Diagnostics); Assert.Equal(before, source.ToJsonString());
        AssertResponse(JsonNode.Parse(converted.Data.GetRawText())!.AsObject(), to);
    }

    /// <summary>文本在结束前到达，函数参数完成后到达，最终结果与源语义一致。</summary>
    [Theory]
    [MemberData(nameof(Routes))]
    public void StreamMatrix_EmitsDeltasAndValidFinalSequence(LlmProtocol from, LlmProtocol to)
    {
        var converter = new LlmProtocolStreamConverter(from, to);
        List<JsonObject> output = [];
        var textBeforeComplete = false;
        foreach (var input in ConversionFixtures.Stream(from))
        {
            var events = converter.Push(input);
            output.AddRange(events.Select(e => JsonNode.Parse(e.Data.GetRawText())!.AsObject()));
            textBeforeComplete |= events.Any(e => e.Data.GetRawText().Contains("你", StringComparison.Ordinal)
                || e.Data.GetRawText().Contains("\\u4F60", StringComparison.OrdinalIgnoreCase));
        }
        Assert.True(textBeforeComplete);
        output.AddRange(converter.Complete().Select(e => JsonNode.Parse(e.Data.GetRawText())!.AsObject()));
        Assert.Empty(converter.Diagnostics);
        var all = JsonSerializer.Serialize(output);
        Assert.Contains("call_1", all); Assert.Contains("lookup", all);
        if (to == LlmProtocol.Responses)
        {
            Assert.Equal("response.created", output[0]["type"]!.GetValue<string>());
            Assert.Equal("response.completed", output[^1]["type"]!.GetValue<string>());
            AssertResponse(output[^1]["response"]!.AsObject(), to);
            Assert.Equal(Enumerable.Range(0, output.Count), output.Select(e => e["sequence_number"]!.GetValue<int>()));
            var items = output.Where(e => e["type"]!.GetValue<string>() == "response.output_item.done").ToArray();
            Assert.Equal(2, items.Length); Assert.Equal("call_1", items[1]["item"]!["call_id"]!.GetValue<string>());
        }
        else if (to == LlmProtocol.ClaudeMessages)
        {
            Assert.Equal("message_start", output[0]["type"]!.GetValue<string>());
            Assert.Equal("message_stop", output[^1]["type"]!.GetValue<string>());
            Assert.Equal(2, output.Count(e => e["type"]!.GetValue<string>() == "content_block_stop"));
            Assert.Equal("tool_use", output[^2]["delta"]!["stop_reason"]!.GetValue<string>());
            Assert.Equal(3, output[^2]["usage"]!["input_tokens"]!.GetValue<int>());
        }
        else if (to == LlmProtocol.ChatCompletions)
        {
            Assert.Equal("tool_calls", output[^1]["choices"]![0]!["finish_reason"]!.GetValue<string>());
            Assert.Equal(5, output[^1]["usage"]!["total_tokens"]!.GetValue<int>());
            Assert.Equal("你好", string.Concat(output.Select(e => e["choices"]![0]!["delta"]!["content"]?.GetValue<string>())));
        }
        else
        {
            Assert.Equal("STOP", output[^1]["candidates"]![0]!["finishReason"]!.GetValue<string>());
            Assert.Equal(5, output[^1]["usageMetadata"]!["totalTokenCount"]!.GetValue<int>());
        }
    }

    /// <summary>跨协议支持按独立预期字段验证，不依赖往返转换自证。</summary>
    private static void AssertResponse(JsonObject response, LlmProtocol protocol)
    {
        Assert.Equal("r1", response[protocol == LlmProtocol.Gemini ? "responseId" : "id"]!.GetValue<string>());
        if (protocol == LlmProtocol.ChatCompletions)
        {
            var message = response["choices"]![0]!["message"]!;
            Assert.Equal("你好", message["content"]!.GetValue<string>());
            Assert.Equal("call_1", message["tool_calls"]![0]!["id"]!.GetValue<string>());
            Assert.Equal("{\"x\":0}", message["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
            Assert.Equal(5, response["usage"]!["total_tokens"]!.GetValue<int>());
        }
        else if (protocol == LlmProtocol.Responses)
        {
            Assert.Equal("你好", response["output"]![0]!["content"]![0]!["text"]!.GetValue<string>());
            Assert.Equal("call_1", response["output"]![1]!["call_id"]!.GetValue<string>());
            Assert.Equal("{\"x\":0}", response["output"]![1]!["arguments"]!.GetValue<string>());
            Assert.Equal(5, response["usage"]!["total_tokens"]!.GetValue<int>());
        }
        else if (protocol == LlmProtocol.ClaudeMessages)
        {
            Assert.Equal("你好", response["content"]![0]!["text"]!.GetValue<string>());
            Assert.Equal("call_1", response["content"]![1]!["id"]!.GetValue<string>());
            Assert.Equal(0, response["content"]![1]!["input"]!["x"]!.GetValue<int>());
            Assert.Equal(2, response["usage"]!["output_tokens"]!.GetValue<int>());
        }
        else
        {
            Assert.Equal("你好", response["candidates"]![0]!["content"]!["parts"]![0]!["text"]!.GetValue<string>());
            Assert.Equal("call_1", response["candidates"]![0]!["content"]!["parts"]![1]!["functionCall"]!["id"]!.GetValue<string>());
            Assert.Equal(0, response["candidates"]![0]!["content"]!["parts"]![1]!["functionCall"]!["args"]!["x"]!.GetValue<int>());
            Assert.Equal(5, response["usageMetadata"]!["totalTokenCount"]!.GetValue<int>());
        }
    }
}
