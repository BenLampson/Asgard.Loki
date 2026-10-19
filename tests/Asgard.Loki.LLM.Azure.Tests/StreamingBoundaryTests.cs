using System.Text;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>缺失、重复、错关联或尾部追加的原生记录不得伪装成正常结束。</summary>
public sealed class StreamingBoundaryTests
{
    /// <summary>未知原生对象不丢弃；finish 后的未知元数据与过滤升级也等待正常 EOF。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnknownNativeMetadataAndDelayedFilter_ArePreserved(bool filtered)
    {
        var extra = filtered ? "{\"id\":\"\",\"model\":\"\",\"choices\":[{\"index\":0,\"finish_reason\":\"content_filter\",\"content_filter_results\":{\"blocked\":true}}]}" : "{\"type\":\"chat.future.metadata\",\"id\":\"metadata-record\",\"future\":{\"integer\":9007199254740993,\"null\":null}}";
        var text = AzureFixture.Sse(false).Replace("data: [DONE]", "data: " + extra + "\n\ndata: [DONE]"); var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(text));
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new AzureHttpFactory(handler); var observed = new List<LlmJsonEvent>();
        await foreach (var item in StreamingTests.DirectStream(new(factory), false, TestContext.Current.CancellationToken)) observed.Add(item);
        Assert.Equal(7, observed.Count); Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(extra), observed[^1].Data)); Assert.True(stream.Eof); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>Chat 必须具有每个 choice 的终态和 DONE，且之后没有生成数据。</summary>
    [Theory] [InlineData("missing-done")] [InlineData("done-only")] [InlineData("unfinished")] [InlineData("partial-choice")] [InlineData("after-done")] [InlineData("after-finish")] [InlineData("duplicate-finish")] [InlineData("identity")] [InlineData("duplicate-index")] [InlineData("index-type")] [InlineData("negative-index")] [InlineData("truncated-frame")]
    public async Task InvalidChatLifecycle_HasNoSuccessfulFinish(string failure)
    {
        var text = AzureFixture.Sse(false).Replace("\r\n", "\n"); var finish = "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";
        text = failure switch
        {
            "missing-done" => text.Replace("data: [DONE]\n\n", ""), "done-only" => "data: [DONE]\n\n",
            "unfinished" => "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"x\"},\"finish_reason\":null}]}\n\ndata: [DONE]\n\n",
            "partial-choice" => "data: {\"choices\":[{\"index\":0,\"finish_reason\":\"stop\"},{\"index\":1,\"finish_reason\":null}]}\n\ndata: [DONE]\n\n",
            "after-done" => text + finish, "after-finish" => text.Replace("data: [DONE]", "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"late\"}}]}\n\ndata: [DONE]"),
            "duplicate-finish" => text.Replace("data: [DONE]", finish + "data: [DONE]"), "identity" => text.Replace("\"model\":\"native-model-version\",\"choices\":[],", "\"model\":\"other-model\",\"choices\":[],"),
            "duplicate-index" => "data: {\"choices\":[{\"index\":0},{\"index\":0}]}\n\n", "index-type" => "data: {\"choices\":[{\"index\":\"0\"}]}\n\n", "negative-index" => "data: {\"choices\":[{\"index\":-1}]}\n\n", _ => text.TrimEnd('\n')
        };
        var observed = await Invalid(text, false); Assert.DoesNotContain(observed, item => item.Data.TryGetProperty("choices", out var choices) && choices.EnumerateArray().Any(choice => choice.TryGetProperty("finish_reason", out var value) && value.ValueKind == JsonValueKind.String));
    }
    /// <summary>Responses 只能以一致的原生终态快照完成，不以 DONE 或 EOF 合成结束。</summary>
    [Theory] [InlineData("missing-terminal")] [InlineData("done-only")] [InlineData("duplicate")] [InlineData("late-event")] [InlineData("identity")] [InlineData("status")] [InlineData("missing-snapshot")] [InlineData("missing-id")]
    public async Task InvalidResponseLifecycle_HasNoSuccessfulTerminal(string failure)
    {
        var text = AzureFixture.Sse(true).Replace("\r\n", "\n"); var start = text.IndexOf("event: response.completed", StringComparison.Ordinal); var terminal = text[start..];
        text = failure switch
        {
            "missing-terminal" => text[..start], "done-only" => "data: [DONE]\n\n", "duplicate" => text + terminal,
            "late-event" => text + "data: {\"type\":\"response.output_text.delta\",\"delta\":\"late\"}\n\n", "identity" => text.Replace("\"id\":\"resp-stream\",\"status\":\"in_progress\"", "\"id\":\"other\",\"status\":\"in_progress\""),
            "status" => text.Replace("\"status\":\"completed\",\"model\"", "\"status\":\"in_progress\",\"model\""), "missing-snapshot" => "data: {\"type\":\"response.completed\"}\n\n",
            _ => "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n"
        };
        var observed = await Invalid(text, true); Assert.DoesNotContain(observed, item => item.Type == "response.completed");
    }
    /// <summary>终态前后都保留原生错误，不重试、不交付此前暂存的成功。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeErrorAfterPossibleTerminal_DoesNotReleaseSuccess(bool responses)
    {
        const string error = "{\"error\":{\"code\":\"late-filter\",\"message\":\"原始错误\"}}"; var text = AzureFixture.Sse(responses).Replace("\r\n", "\n"); text = responses ? text + "data: " + error + "\n\n" : text.Replace("data: [DONE]", "data: " + error + "\n\ndata: [DONE]");
        var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(text)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new AzureHttpFactory(handler); var observed = new List<LlmJsonEvent>();
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { await foreach (var item in StreamingTests.DirectStream(new(factory), responses, TestContext.Current.CancellationToken)) observed.Add(item); });
        Assert.Equal(error, failure.ResponseBody); Assert.Equal(responses ? 6 : 3, observed.Count); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原始字节限制也覆盖 SSE 的注释、行和所有事件，精确边界可用。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task TotalSseByteLimit_IsExact(bool responses, bool tooSmall)
    {
        var bytes = Encoding.UTF8.GetBytes(AzureFixture.Sse(responses)); var stream = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new AzureHttpFactory(handler); var provider = new AzureLlmProvider(factory, new() { MaximumBodyBytes = bytes.Length - (tooSmall ? 1 : 0) });
        async Task Consume() { await foreach (var _ in StreamingTests.DirectStream(provider, responses, TestContext.Current.CancellationToken)) { } }
        if (tooSmall) await Assert.ThrowsAsync<InvalidDataException>(Consume); else await Consume(); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>有限暂存包括 finish、过滤和 usage，超限不能丢尾部获得成功。</summary>
    [Theory] [InlineData(3)] [InlineData(2)]
    public async Task TerminalTailLimit_DoesNotDropRecords(int maximum)
    {
        var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(AzureFixture.Sse(false))); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new AzureHttpFactory(handler); var provider = new AzureLlmProvider(factory, new() { MaximumBufferedTerminalEvents = maximum }); var count = 0;
        async Task Consume() { await foreach (var _ in StreamingTests.DirectStream(provider, false, TestContext.Current.CancellationToken)) count++; }
        if (maximum == 2) { await Assert.ThrowsAsync<InvalidDataException>(Consume); Assert.Equal(3, count); } else { await Consume(); Assert.Equal(6, count); }
        Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>原始 choice 索引可以稀疏且不重编号，关联数上限按实际数量计算。</summary>
    [Theory] [InlineData(2)] [InlineData(1)]
    public async Task MultipleChoiceAssociation_PreservesIndicesAndLimits(int maximum)
    {
        const string text = "data: {\"choices\":[{\"index\":2,\"finish_reason\":\"stop\"},{\"index\":7,\"finish_reason\":\"content_filter\",\"content_filter_results\":{\"filtered\":true}}]}\n\ndata: [DONE]\n\n";
        var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(text)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new AzureHttpFactory(handler); var provider = new AzureLlmProvider(factory, new() { MaximumTrackedChoices = maximum }); var observed = new List<LlmJsonEvent>();
        async Task Consume() { await foreach (var item in StreamingTests.DirectStream(provider, false, TestContext.Current.CancellationToken)) observed.Add(item); }
        if (maximum == 1) { await Assert.ThrowsAsync<InvalidDataException>(Consume); Assert.Empty(observed); } else { await Consume(); var choices = Assert.Single(observed).Data.GetProperty("choices"); Assert.Equal(2, choices[0].GetProperty("index").GetInt32()); Assert.Equal(7, choices[1].GetProperty("index").GetInt32()); }
        Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    private static async Task<List<LlmJsonEvent>> Invalid(string text, bool responses)
    {
        var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(text)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new AzureHttpFactory(handler); var observed = new List<LlmJsonEvent>();
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in StreamingTests.DirectStream(new(factory), responses, TestContext.Current.CancellationToken)) observed.Add(item); });
        Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls); return observed;
    }
}
