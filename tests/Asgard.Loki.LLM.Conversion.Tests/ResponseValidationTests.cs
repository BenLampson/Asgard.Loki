using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>验证结果结构与完整参数，不把类型错误当成字段缺失。</summary>
public sealed class ResponseValidationTests
{
    /// <summary>非对象 usage 不能被类型转换静默丢弃。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions, false)] [InlineData(LlmProtocol.Responses, false)]
    [InlineData(LlmProtocol.ClaudeMessages, false)] [InlineData(LlmProtocol.Gemini, false)]
    [InlineData(LlmProtocol.ChatCompletions, true)] [InlineData(LlmProtocol.Responses, true)]
    [InlineData(LlmProtocol.ClaudeMessages, true)] [InlineData(LlmProtocol.Gemini, true)]
    public void MalformedUsage_IsRejected(LlmProtocol protocol, bool streaming)
    {
        var target = protocol == LlmProtocol.Responses ? LlmProtocol.ChatCompletions : LlmProtocol.Responses;
        var usageName = protocol == LlmProtocol.Gemini ? "usageMetadata" : "usage";
        if (!streaming)
        {
            var response = ConversionFixtures.Response(protocol); response[usageName] = "invalid";
            Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertResponse(response, protocol, target));
            return;
        }
        var first = ConversionFixtures.Stream(protocol).First();
        var container = protocol == LlmProtocol.ClaudeMessages ? first["message"]!.AsObject()
            : protocol == LlmProtocol.Responses ? first["response"]!.AsObject() : first;
        container[usageName] = "invalid";
        var state = new LlmProtocolStreamConverter(protocol, target);
        Assert.Throws<ArgumentException>(() => state.Push(first));
    }

    /// <summary>Chat 与 Responses 互转也必须验证完整工具 JSON，不仅在转换为对象时验证。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions, false)] [InlineData(LlmProtocol.Responses, false)]
    [InlineData(LlmProtocol.ChatCompletions, true)] [InlineData(LlmProtocol.Responses, true)]
    public void InvalidToolArguments_AreRejected(LlmProtocol protocol, bool response)
    {
        var source = response ? ConversionFixtures.Response(protocol) : ConversionFixtures.Request(protocol);
        var arguments = protocol == LlmProtocol.Responses ? source[response ? "output" : "input"]![1]!
            : response ? source["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!
            : source["messages"]![2]!["tool_calls"]![0]!["function"]!;
        arguments["arguments"] = "{";
        var target = protocol == LlmProtocol.ChatCompletions ? LlmProtocol.Responses : LlmProtocol.ChatCompletions;
        Assert.ThrowsAny<JsonException>(() =>
        {
            if (response) LlmProtocolConverter.ConvertResponse(source, protocol, target);
            else LlmProtocolConverter.ConvertRequest(source, protocol, target);
        });
    }

    /// <summary>重复候选索引也属于多个候选，不能合并成一条成功响应。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Gemini)]
    public void DuplicateCandidateWithinEvent_IsRejected(LlmProtocol protocol)
    {
        var first = ConversionFixtures.Stream(protocol).First();
        var choices = first[protocol == LlmProtocol.Gemini ? "candidates" : "choices"]!.AsArray();
        choices.Add(choices[0]!.DeepClone());
        var state = new LlmProtocolStreamConverter(protocol, LlmProtocol.Responses);
        Assert.Throws<NotSupportedException>(() => state.Push(first));
    }

    /// <summary>生成流不能把用户角色的内容包装为模型输出。</summary>
    [Fact]
    public void GeminiWrongOutputRole_IsRejected()
    {
        var first = ConversionFixtures.Stream(LlmProtocol.Gemini).First();
        first["candidates"]![0]!["content"]!["role"] = "user";
        var state = new LlmProtocolStreamConverter(LlmProtocol.Gemini, LlmProtocol.Responses);
        Assert.Throws<InvalidDataException>(() => state.Push(first));
    }
}
