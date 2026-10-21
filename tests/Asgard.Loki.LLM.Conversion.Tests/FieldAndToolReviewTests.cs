using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>针对已识别但未消费的字段与工具生命周期补充独立回归用例。</summary>
public sealed class FieldAndToolReviewTests
{
    /// <summary>结构无效或无法表达的嵌套字段不能静默消失。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    public void RecognizedButUnconsumedFields_AreRejected(int scenario)
    {
        var protocol = scenario is >= 2 and <= 6 ? LlmProtocol.Gemini : scenario == 7 ? LlmProtocol.ClaudeMessages
            : scenario == 10 ? LlmProtocol.Responses : LlmProtocol.ChatCompletions;
        var request = ConversionFixtures.Request(protocol);
        switch (scenario)
        {
            case 0: request["messages"]![0]!["tool_call_id"] = "unconsumed"; break;
            case 1: request["messages"]![3]!["tool_calls"] = new JsonArray(); break;
            case 2: request["generationConfig"] = null; break;
            case 3: request["systemInstruction"] = null; break;
            case 4: request["toolConfig"] = null; break;
            case 5: request["tools"]![0]!["functionDeclarations"] = null; break;
            case 6: request["toolConfig"]!["functionCallingConfig"]!["allowedFunctionNames"] = null; break;
            case 7: request["messages"]![0]!["content"]![1]!["source"]!["url"] = "https://ignored.invalid/image"; break;
            case 8: request["messages"]![1]!["content"]![1]!["detail"] = "high"; break;
            case 9: request["tools"]![0]!["function"]!["type"] = "unknown"; break;
            case 10: request["text"] = JsonNode.Parse("""{"format":{"type":"text","name":"lost"}}"""); break;
            case 11: request["messages"]![1]!["content"]![0]!["annotations"] = new JsonObject(); break;
        }
        void Convert() => LlmProtocolConverter.ConvertRequest(request, protocol,
            protocol == LlmProtocol.Responses ? LlmProtocol.ChatCompletions : LlmProtocol.Responses);
        if (scenario is 0 or 1 or 7 or 8 or 9 or 10) Assert.Throws<LlmConversionException>(Convert);
        else Assert.Throws<ArgumentException>(Convert);
    }

    /// <summary>同一工具结果不允许被重复关联。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)]
    [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void DuplicateToolResults_AreRejected(LlmProtocol protocol)
    {
        var request = ConversionFixtures.Request(protocol);
        var items = request[protocol == LlmProtocol.Gemini ? "contents" : protocol == LlmProtocol.Responses ? "input" : "messages"]!.AsArray();
        var resultIndex = protocol == LlmProtocol.ChatCompletions ? 3 : 2;
        items.Insert(resultIndex + 1, items[resultIndex]!.DeepClone());
        Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(request, protocol,
            protocol == LlmProtocol.ChatCompletions ? LlmProtocol.Responses : LlmProtocol.ChatCompletions));
    }

    /// <summary>同名函数顺序调用应只匹配尚未回填的调用，不受已完成历史干扰。</summary>
    [Fact]
    public void RepeatedGeminiFunctionNames_LinkOnlyPendingCalls()
    {
        var request = ConversionFixtures.Request(LlmProtocol.Gemini); var contents = request["contents"]!.AsArray();
        contents[1]!["parts"]![0]!["functionCall"]!.AsObject().Remove("id");
        contents[2]!["parts"]![0]!["functionResponse"]!.AsObject().Remove("id");
        contents.Add(contents[1]!.DeepClone()); contents.Add(contents[2]!.DeepClone());
        var result = LlmProtocolConverter.ConvertRequest(request, LlmProtocol.Gemini, LlmProtocol.ChatCompletions);
        var messages = JsonNode.Parse(result.Data.GetRawText())!["messages"]!.AsArray();
        var calls = messages.Where(m => m?["tool_calls"] is not null).ToArray();
        var results = messages.Where(m => m?["role"]?.GetValue<string>() == "tool").ToArray();
        Assert.Equal(2, calls.Length); Assert.Equal(2, results.Length);
        var first = calls[0]!["tool_calls"]![0]!["id"]!.GetValue<string>();
        var second = calls[1]!["tool_calls"]![0]!["id"]!.GetValue<string>();
        Assert.NotEqual(first, second);
        Assert.Equal(first, results[0]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal(second, results[1]!["tool_call_id"]!.GetValue<string>());
    }

    /// <summary>生成的 Gemini 关联 ID 不能撞上后续原生 ID，也不能改写原生 ID。</summary>
    [Fact]
    public void GeneratedGeminiIds_DoNotCollideWithExplicitIds()
    {
        var request = ConversionFixtures.Request(LlmProtocol.Gemini); var contents = request["contents"]!.AsArray();
        contents[1]!["parts"]![0]!["functionCall"]!.AsObject().Remove("id");
        contents[2]!["parts"]![0]!["functionResponse"]!.AsObject().Remove("id");
        var call = contents[1]!.DeepClone(); call["parts"]![0]!["functionCall"]!["id"] = "loki_call_0";
        var output = contents[2]!.DeepClone(); output["parts"]![0]!["functionResponse"]!["id"] = "loki_call_0";
        contents.Add(call); contents.Add(output);
        var converted = LlmProtocolConverter.ConvertRequest(request, LlmProtocol.Gemini, LlmProtocol.ChatCompletions);
        var calls = JsonNode.Parse(converted.Data.GetRawText())!["messages"]!.AsArray().Where(m => m?["tool_calls"] is not null).ToArray();
        Assert.Equal("loki_call_0", calls[1]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.NotEqual("loki_call_0", calls[0]!["tool_calls"]![0]!["id"]!.GetValue<string>());
    }
}
