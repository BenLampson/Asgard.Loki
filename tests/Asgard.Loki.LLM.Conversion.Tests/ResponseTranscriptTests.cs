using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>使用包含所有正常 done 事件的独立记录，验证正常与损坏的协议生命周期。</summary>
public sealed class ResponseTranscriptTests
{
    /// <summary>完整有效的内容块和工具完成序列不能被新增校验误拒绝。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void ValidTranscript_ConvertsEveryDoneEvent(LlmProtocol target)
    {
        var state = new LlmProtocolStreamConverter(LlmProtocol.Responses, target);
        List<JsonObject> output = [];
        foreach (var item in Transcript()) output.AddRange(state.Push(item).Select(e => JsonNode.Parse(e.Data.GetRawText())!.AsObject()));
        output.AddRange(state.Complete().Select(e => JsonNode.Parse(e.Data.GetRawText())!.AsObject()));
        Assert.Empty(state.Diagnostics);
        if (target == LlmProtocol.ChatCompletions)
        {
            Assert.Equal("你好", string.Concat(output.Select(e => e["choices"]![0]!["delta"]!["content"]?.GetValue<string>())));
            var call = output.Single(e => e["choices"]![0]!["delta"]!["tool_calls"] is not null)["choices"]![0]!["delta"]!["tool_calls"]![0]!;
            Assert.Equal("call_1", call["id"]!.GetValue<string>()); Assert.Equal("{\"x\":0}", call["function"]!["arguments"]!.GetValue<string>());
            Assert.Equal("tool_calls", output[^1]["choices"]![0]!["finish_reason"]!.GetValue<string>());
        }
        else if (target == LlmProtocol.ClaudeMessages)
        {
            Assert.Equal(new[] { 0, 1 }, output.Where(e => e["type"]!.GetValue<string>() == "content_block_start").Select(e => e["index"]!.GetValue<int>()));
            Assert.Equal("message_stop", output[^1]["type"]!.GetValue<string>());
        }
        else Assert.Equal("STOP", output[^1]["candidates"]![0]!["finishReason"]!.GetValue<string>());
    }

    /// <summary>完成项目、最终 ID、序号、事件名与内容必须同时一致。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ContradictoryTranscript_IsRejected(int change)
    {
        var events = Transcript();
        switch (change)
        {
            case 0: events.First(e => e["type"]!.GetValue<string>() == "response.output_item.done")["item"]!["id"] = "foreign"; break;
            case 1: events[^1]["response"]!["output"]![0]!["id"] = "foreign"; break;
            case 2: events[1]["sequence_number"] = events[0]["sequence_number"]!.DeepClone(); break;
            case 3: events[^1]["type"] = "response.incomplete"; break;
            case 4: events[1]["item"]!["role"] = "user"; break;
        }
        var state = new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.ChatCompletions);
        Assert.Throws<InvalidDataException>(() => { foreach (var item in events) state.Push(item); state.Complete(); });
    }

    /// <summary>已知开始快照中的未来字段也必须进入诊断。</summary>
    [Fact]
    public void UnknownFieldInsideKnownItem_IsDiagnosed()
    {
        var events = Transcript(); events[1]["item"]!["future"] = false;
        var state = new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.ChatCompletions);
        state.Push(events[0]);
        var error = Assert.Throws<LlmConversionException>(() => state.Push(events[1]));
        Assert.Contains(error.Diagnostics, d => d.Path == "$event.item.future");
    }

    /// <summary>缓冲中的前序工具不能被后续文本越过并重排到另一个输出索引。</summary>
    [Theory]
    [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void TextOvertakesUnfinishedTool_IsRejected(LlmProtocol target)
    {
        var state = new LlmProtocolStreamConverter(LlmProtocol.ChatCompletions, target);
        state.Push(JsonNode.Parse("""{"id":"r","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"c","function":{"name":"f","arguments":"{\"x\":"}}]}}]}""")!.AsObject());
        Assert.Throws<InvalidDataException>(() => state.Push(JsonNode.Parse("""{"id":"r","model":"m","choices":[{"index":0,"delta":{"content":"late"}}]}""")!.AsObject()));
    }

    /// <summary>独立用量尾块合法，不能因其没有 candidates 被误认为第二次结束。</summary>
    [Fact]
    public void GeminiUsageOnlyTail_IsAllowed()
    {
        var state = new LlmProtocolStreamConverter(LlmProtocol.Gemini, LlmProtocol.Responses);
        foreach (var item in ConversionFixtures.Stream(LlmProtocol.Gemini)) state.Push(item);
        state.Push(JsonNode.Parse("""{"responseId":"r1","modelVersion":"model-a","usageMetadata":{"promptTokenCount":4,"candidatesTokenCount":2,"totalTokenCount":6}}""")!.AsObject());
        Assert.Equal(6, state.Complete().Last().Data.GetProperty("response").GetProperty("usage").GetProperty("total_tokens").GetInt64());
    }

    /// <summary>显式长度终态会输出 incomplete，不能把输出项标记为 completed。</summary>
    [Fact]
    public void ExplicitLengthFinish_KeepsIncompleteOutputStatus()
    {
        var events = ConversionFixtures.Stream(LlmProtocol.ChatCompletions).ToArray();
        events[^2]["choices"]![0]!["finish_reason"] = "length";
        var state = new LlmProtocolStreamConverter(LlmProtocol.ChatCompletions, LlmProtocol.Responses);
        foreach (var item in events) state.Push(item);
        var final = state.Complete().Last().Data;
        Assert.Equal("response.incomplete", final.GetProperty("type").GetString());
        Assert.All(final.GetProperty("response").GetProperty("output").EnumerateArray(), item => Assert.Equal("incomplete", item.GetProperty("status").GetString()));
    }

    /// <summary>工具终态与真实输出矛盾时拒绝推断修复。</summary>
    [Fact]
    public void FinishReasonContradictsToolCalls_IsRejected()
    {
        var response = ConversionFixtures.Response(LlmProtocol.ChatCompletions); response["choices"]![0]!["finish_reason"] = "stop";
        Assert.Throws<InvalidDataException>(() => LlmProtocolConverter.ConvertResponse(response, LlmProtocol.ChatCompletions, LlmProtocol.Responses));
    }

    /// <summary>手工补齐正确的内容块及项目完成事件，保留真实 item_id 与单调序号。</summary>
    private static List<JsonObject> Transcript()
    {
        var source = ConversionFixtures.Stream(LlmProtocol.Responses).ToArray();
        var response = ConversionFixtures.Response(LlmProtocol.Responses);
        List<JsonObject> events = [source[0], source[1], new()
        {
            ["type"] = "response.content_part.added", ["output_index"] = 0, ["content_index"] = 0, ["item_id"] = "msg1",
            ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = "", ["annotations"] = new JsonArray() }
        }, source[2], source[3], new()
        {
            ["type"] = "response.output_text.done", ["output_index"] = 0, ["content_index"] = 0, ["item_id"] = "msg1", ["text"] = "你好"
        }, new()
        {
            ["type"] = "response.content_part.done", ["output_index"] = 0, ["content_index"] = 0, ["item_id"] = "msg1",
            ["part"] = response["output"]![0]!["content"]![0]!.DeepClone()
        }, new() { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = response["output"]![0]!.DeepClone() },
        source[4], source[5], source[6], source[7],
        new() { ["type"] = "response.output_item.done", ["output_index"] = 1, ["item"] = response["output"]![1]!.DeepClone() }, source[8]];
        for (var i = 0; i < events.Count; i++) events[i]["sequence_number"] = i;
        return events;
    }
}
