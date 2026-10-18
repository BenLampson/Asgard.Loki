using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>验证损坏或缺失的流生命周期不能产生成功终态或错误工具参数。</summary>
public sealed class StreamReviewTests
{
    /// <summary>Claude 必须开始消息、关闭内容块并提供结束原因。</summary>
    [Theory]
    [InlineData("message_start")] [InlineData("message_delta")] [InlineData("content_block_stop")]
    public void ClaudeMissingLifecycleEvent_RejectsSuccess(string missing)
    {
        var stream = new LlmProtocolStreamConverter(LlmProtocol.ClaudeMessages, LlmProtocol.Responses);
        Assert.Throws<InvalidDataException>(() =>
        {
            foreach (var item in ConversionFixtures.Stream(LlmProtocol.ClaudeMessages).Where(e => e["type"]!.GetValue<string>() != missing)) stream.Push(item);
            stream.Complete();
        });
    }

    /// <summary>已经关闭的 Claude 块不能继续接收增量。</summary>
    [Fact]
    public void ClaudeDeltaAfterBlockStop_IsRejected()
    {
        var stream = new LlmProtocolStreamConverter(LlmProtocol.ClaudeMessages, LlmProtocol.Responses);
        var events = ConversionFixtures.Stream(LlmProtocol.ClaudeMessages).ToArray();
        foreach (var item in events.Take(5)) stream.Push(item);
        Assert.Throws<InvalidDataException>(() => stream.Push(events[3]));
    }

    /// <summary>Responses done 的文本和关联身份必须匹配真实增量。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ResponsesDone_MustMatchTextAndItemId(bool wrongId)
    {
        var stream = new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.ChatCompletions);
        foreach (var item in ConversionFixtures.Stream(LlmProtocol.Responses).Take(4)) stream.Push(item);
        Assert.Throws<InvalidDataException>(() => stream.Push(new()
        {
            ["type"] = "response.output_text.done", ["output_index"] = 0, ["content_index"] = 0,
            ["item_id"] = wrongId ? "foreign" : "msg1", ["text"] = wrongId ? "你好" : "changed"
        }));
    }

    /// <summary>完成后的工具参数不能被追加，再与旧参数一起宣告成功。</summary>
    [Fact]
    public void ResponsesArgumentsAfterDone_AreRejected()
    {
        var stream = new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.ChatCompletions);
        foreach (var item in ConversionFixtures.Stream(LlmProtocol.Responses).TakeWhile(e => e["type"]!.GetValue<string>() != "response.completed")) stream.Push(item);
        Assert.Throws<InvalidDataException>(() => stream.Push(new() { ["type"] = "response.function_call_arguments.delta", ["output_index"] = 1, ["delta"] = " " }));
    }

    /// <summary>已知 done 事件的未知嵌套字段也必须形成损耗诊断。</summary>
    [Fact]
    public void ResponsesDoneUnknownFields_AreNotIgnored()
    {
        var stream = new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.ChatCompletions);
        foreach (var item in ConversionFixtures.Stream(LlmProtocol.Responses).Take(4)) stream.Push(item);
        Assert.Throws<LlmConversionException>(() => stream.Push(new()
        {
            ["type"] = "response.content_part.done", ["output_index"] = 0, ["content_index"] = 0,
            ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = "你好", ["future"] = false }
        }));
    }

    /// <summary>终态之后不能再次结束或改写状态。</summary>
    [Theory]
    [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Responses)]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Gemini)]
    public void DuplicateTerminal_IsRejected(LlmProtocol source)
    {
        var stream = new LlmProtocolStreamConverter(source, source == LlmProtocol.Responses ? LlmProtocol.ChatCompletions : LlmProtocol.Responses);
        var events = ConversionFixtures.Stream(source).ToArray();
        foreach (var item in events) stream.Push(item);
        var terminal = source == LlmProtocol.ChatCompletions ? events[^2] : events[^1];
        Assert.Throws<InvalidDataException>(() => stream.Push(terminal));
    }

    /// <summary>显式允许未知字段损耗时，仍然必须输出已知的文本。</summary>
    [Fact]
    public void GeminiAllowedUnknownPartField_PreservesText()
    {
        var stream = new LlmProtocolStreamConverter(LlmProtocol.Gemini, LlmProtocol.Responses, new() { AllowLoss = true });
        var events = ConversionFixtures.Stream(LlmProtocol.Gemini).ToArray();
        events[1]["candidates"]![0]!["content"]!["parts"]![0]!["future"] = false;
        foreach (var item in events) stream.Push(item);
        var final = stream.Complete().Last().Data.GetProperty("response");
        Assert.Equal("你好", final.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString());
    }
}
