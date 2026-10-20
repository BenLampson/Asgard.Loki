using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Perplexity.Tests.SonarFixture;

namespace Asgard.Loki.LLM.Perplexity.Tests;

/// <summary>Sonar 原生选择关联、结束形式和元数据生命周期，不制造选择结束原因。</summary>
public sealed class SonarLifecycleTests
{
    /// <summary>原生全局结束、选择结束和实际控制标记分别保留，不从 EOF 猜测完成。</summary>
    [Theory] [InlineData("global")] [InlineData("global-done")] [InlineData("choices")] [InlineData("choices-done")] [InlineData("global-no-choice-finish")] [InlineData("done-no-choice-finish")]
    public async Task NativeEndings_ArePreserved(string mode)
    {
        var events = Golden.GetProperty("events").EnumerateArray().Select(e => JsonNode.Parse(e.GetRawText())!).ToList();
        if (mode.StartsWith("choices", StringComparison.Ordinal) || mode == "done-no-choice-finish") events.RemoveAt(3);
        if (mode.EndsWith("no-choice-finish", StringComparison.Ordinal)) events[2]["choices"]![0]!.AsObject().Remove("finish_reason");
        var source = string.Concat(events.Select(e => Frame(JsonSerializer.SerializeToElement(e))))+(mode.EndsWith("done", StringComparison.Ordinal) || mode == "done-no-choice-finish" ? "data: [DONE]\n\n" : "");
        var seen = await Read(source); Assert.Equal(events.Count, seen.Count); for (var i = 0; i < seen.Count; i++) Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(events[i]), seen[i].Data));
    }
    /// <summary>显式全局终态可单独结束一个空输出，SDK 不添加 choice 或消息。</summary>
    [Fact]
    public async Task GlobalOnly_IsNotExpanded()
    { var data = Golden.GetProperty("events")[3]; var seen = await Read(Frame(data)); Assert.True(JsonElement.DeepEquals(data, Assert.Single(seen).Data)); }
    /// <summary>稀疏精确索引、多个选择独立结束和工具参数分片保持原文，不能相互误关联。</summary>
    [Fact]
    public async Task SparseParallelChoices_EndIndependently()
    {
        const string source = "data: {\"id\":\"native\",\"choices\":[{\"index\":9007199254740993,\"delta\":{\"tool_calls\":[{\"index\":3,\"id\":\"call-large\",\"function\":{\"name\":\"lookup\",\"arguments\":\"{\"}}]},\"finish_reason\":null},{\"index\":2,\"delta\":{\"content\":\"two\"},\"finish_reason\":null}]}\n\ndata: {\"id\":\"native\",\"choices\":[{\"index\":2,\"delta\":{},\"finish_reason\":\"length\"}]}\n\ndata: {\"id\":\"native\",\"choices\":[{\"index\":9007199254740993,\"delta\":{\"tool_calls\":[{\"index\":3,\"function\":{\"arguments\":\"}宽\"}}]},\"finish_reason\":\"stop\"}]}\n\n";
        var seen = await Read(source); Assert.Equal(3, seen.Count); Assert.Equal(9007199254740993L, seen[^1].Data.GetProperty("choices")[0].GetProperty("index").GetInt64()); Assert.Equal("}宽", seen[^1].Data.GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString());
    }
    /// <summary>原生全局结束帧可携带一次最终消息快照，不重复交付或重编号数据。</summary>
    [Fact]
    public async Task GlobalFinalSnapshot_IsPreserved()
    {
        var final = JsonNode.Parse(Golden.GetProperty("events")[3].GetRawText())!.AsObject(); var choice = JsonNode.Parse(Golden.GetProperty("result").GetProperty("choices")[0].GetRawText())!; choice["delta"] = new JsonObject(); final["choices"] = new JsonArray(choice);
        var source = string.Concat(Golden.GetProperty("events").EnumerateArray().Take(3).Select(Frame))+Frame(JsonSerializer.SerializeToElement(final)); var seen = await Read(source); Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(final), seen[^1].Data));
    }
    /// <summary>最终快照不能用全局终态掩盖已结束选择的新内容或不同结束原因。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ContradictingGlobalSnapshot_DiscardsEndings(bool content)
    {
        var final = JsonNode.Parse(Golden.GetProperty("events")[3].GetRawText())!.AsObject(); var choice = JsonNode.Parse(Golden.GetProperty("result").GetProperty("choices")[0].GetRawText())!; choice["delta"] = content ? new JsonObject { ["content"] = "late generation" } : new JsonObject(); if (!content) choice["finish_reason"] = "length"; final["choices"] = new JsonArray(choice);
        var source = Frame(Golden.GetProperty("events")[2])+Frame(JsonSerializer.SerializeToElement(final)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(source)), true))); var factory = new PerplexityHttpFactory(handler); List<ChatCompletionEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => Drain(new(factory), TestContext.Current.CancellationToken, seen)); Assert.Empty(seen); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>截断、重开、重复选择、假结束、身份漂移和关联上限均明确失败。</summary>
    [Theory] [InlineData("empty")] [InlineData("unfinished")] [InlineData("unended-choice")] [InlineData("early-done")] [InlineData("duplicate-done")] [InlineData("after-done")] [InlineData("duplicate-global")] [InlineData("reopen-global")] [InlineData("reopen-choice")] [InlineData("repeat-choice-finish")] [InlineData("id-drift")] [InlineData("bad-id")] [InlineData("choices-kind")] [InlineData("duplicate-index")] [InlineData("negative-index")] [InlineData("float-index")] [InlineData("bad-finish")] [InlineData("bad-delta")] [InlineData("bad-status")] [InlineData("pending-global")] [InlineData("bad-type")] [InlineData("choice-budget")] [InlineData("tail-budget")]
    public async Task InvalidNativeState_DiscardsEndings(string fault)
    {
        var initial = Frame(Golden.GetProperty("events")[0]); var global = Frame(Golden.GetProperty("events")[3]); var finish = Frame(Golden.GetProperty("events")[2]);
        var source = fault switch
        {
            "empty" => "", "unfinished" => initial, "unended-choice" => finish+"data: {\"choices\":[{\"index\":1,\"delta\":{\"content\":\"unfinished\"}}]}\n\n", "early-done" => "data: [DONE]\n\n",
            "duplicate-done" => global+"data: [DONE]\n\ndata: [DONE]\n\n", "after-done" => global+"data: [DONE]\n\ndata: {\"type\":\"vendor.trace\"}\n\n", "duplicate-global" => global+global,
            "reopen-global" => global+initial, "reopen-choice" => finish+initial, "repeat-choice-finish" => finish+finish,
            "id-drift" => initial+"data: {\"id\":\"other\",\"type\":\"end_of_stream\",\"choices\":[]}\n\n", "bad-id" => "data: {\"id\":false,\"type\":\"end_of_stream\",\"choices\":[]}\n\n",
            "choices-kind" => "data: {\"type\":\"info\",\"choices\":{}}\n\n", "duplicate-index" => "data: {\"choices\":[{\"index\":0},{\"index\":0}]}\n\n",
            "negative-index" => "data: {\"choices\":[{\"index\":-1}]}\n\n", "float-index" => "data: {\"choices\":[{\"index\":1.5}]}\n\n", "bad-finish" => "data: {\"choices\":[{\"index\":0,\"finish_reason\":false}]}\n\n",
            "bad-delta" => "data: {\"choices\":[{\"index\":0,\"delta\":[]}]}\n\n", "bad-status" => "data: {\"type\":\"end_of_stream\",\"status\":false}\n\n", "pending-global" => "data: {\"type\":\"end_of_stream\",\"status\":\"PENDING\"}\n\n",
            "bad-type" => "data: {\"type\":false,\"choices\":[]}\n\n", "choice-budget" => initial+"data: {\"choices\":[{\"index\":2,\"finish_reason\":\"stop\"}]}\n\n",
            _ => global+"data: {\"type\":\"vendor.trace\"}\n\n"
        };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(source)), true))); var factory = new PerplexityHttpFactory(handler); List<ChatCompletionEvent> seen = []; var options = fault switch { "choice-budget" => new PerplexitySonarOptions { MaximumTrackedChoices = 1 }, "tail-budget" => new() { MaximumBufferedTerminalEvents = 1 }, _ => new() };
        await Assert.ThrowsAsync<InvalidDataException>(() => Drain(new(factory, options), TestContext.Current.CancellationToken, seen)); Assert.DoesNotContain(seen, Ending); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生 length 保持原文，不被改成 stop；新未知结束原因仍作为原生数据。</summary>
    [Theory] [InlineData("length")] [InlineData("vendor_end")]
    public async Task NativeFinish_RemainsOriginal(string finish)
    {
        var data = JsonNode.Parse(Golden.GetProperty("events")[2].GetRawText())!; data["choices"]![0]!["finish_reason"] = finish; var seen = await Read(Frame(JsonSerializer.SerializeToElement(data))); Assert.Equal(finish, Assert.Single(seen).Data.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }
    /// <summary>未知业务元数据及原生 info 尾帧不覆盖生成身份或触发 Responses 状态机。</summary>
    [Fact]
    public async Task MetadataAfterGlobal_RemainsOriginal()
    {
        var metadata = JsonSerializer.Deserialize<JsonElement>("{\"type\":\"response.failed\",\"id\":\"business\",\"error\":{\"business\":false},\"choices\":{\"business\":true}}"); var info = Golden.GetProperty("events")[1]; var seen = await Read(Frame(Golden.GetProperty("events")[3])+Frame(metadata)+Frame(info)); Assert.Equal(3, seen.Count); Assert.True(JsonElement.DeepEquals(metadata, seen[1].Data)); Assert.True(JsonElement.DeepEquals(info, seen[2].Data));
    }
    /// <summary>端点枚举和稀疏选择预算必须是有效策略，在工厂创建前拒绝。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void InvalidExplicitPolicy_IsRejected(bool endpoint)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new PerplexityHttpFactory(handler); Assert.Throws<ArgumentOutOfRangeException>(() => new PerplexitySonarProvider(factory, endpoint ? new() { Endpoint = (PerplexitySonarEndpoint)999 } : new() { MaximumTrackedChoices = 0 })); Assert.Empty(factory.Clients);
    }
    private static async Task<List<ChatCompletionEvent>> Read(string source)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(source)), true))); var factory = new PerplexityHttpFactory(handler); List<ChatCompletionEvent> seen = []; await Drain(new(factory), TestContext.Current.CancellationToken, seen); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls); return seen;
    }
}
