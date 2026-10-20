using System.Text;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>文本补全 SSE 的原生 choice/text/logprobs 终态、有限尾部和资源关闭，不能借用 Chat delta 推断。</summary>
public sealed class AzureTextStreamBoundaryTests
{
    private const string Start = "data: {\"id\":\"cmpl\",\"model\":\"native\",\"object\":\"text_completion\",\"choices\":[{\"index\":0,\"text\":\"first\",\"finish_reason\":null}]}\n\n";
    private const string Finish = "data: {\"id\":\"cmpl\",\"model\":\"native\",\"choices\":[{\"index\":0,\"text\":\"\",\"finish_reason\":\"stop\"}]}\n\n";
    private const string Done = "data: [DONE]\n\n";
    /// <summary>损坏/重复/缺失/晚到终态或结束后的 text/logprobs 都失败，不发布暂存的结束尾部。</summary>
    [Theory] [InlineData("missing-done")] [InlineData("missing-finish")] [InlineData("missing-choice-finish")]
    [InlineData("repeated-finish")] [InlineData("late-text")] [InlineData("late-logprobs")] [InlineData("changed-id")] [InlineData("changed-model")]
    [InlineData("duplicate-index")] [InlineData("negative-index")] [InlineData("bad-finish")] [InlineData("truncated-event")]
    [InlineData("after-done")] [InlineData("choice-limit")] [InlineData("tail-limit")] [InlineData("wrong-object")] [InlineData("bad-logprobs")] [InlineData("fake-filter-null")] [InlineData("fake-filter-bool")]
    public async Task InvalidNativeLifecycleCannotPublishFinishedTail(string kind)
    {
        var raw = kind switch
        {
            "missing-done" => Start + Finish,
            "missing-finish" => Start + Done,
            "missing-choice-finish" => Start + Frame("{\"choices\":[{\"index\":1,\"text\":\"other\",\"finish_reason\":null}]}") + Finish + Done,
            "repeated-finish" => Start + Finish + Finish + Done,
            "late-text" => Start + Finish + Frame("{\"choices\":[{\"index\":0,\"text\":\"late\",\"finish_reason\":null}]}") + Done,
            "late-logprobs" => Start + Finish + Frame("{\"choices\":[{\"index\":0,\"text\":\"\",\"logprobs\":{\"tokens\":[\"late\"]}}]}") + Done,
            "changed-id" => Start + Frame("{\"id\":\"other\",\"choices\":[]}") + Finish + Done,
            "changed-model" => Start + Frame("{\"model\":\"other\",\"choices\":[]}") + Finish + Done,
            "duplicate-index" => Start + Frame("{\"choices\":[{\"index\":0},{\"index\":0}]}") + Finish + Done,
            "negative-index" => Start + Frame("{\"choices\":[{\"index\":-1}]}") + Finish + Done,
            "bad-finish" => Start + Frame("{\"choices\":[{\"index\":0,\"finish_reason\":false}]}") + Done,
            "truncated-event" => Start + Finish + "data: {",
            "after-done" => Start + Finish + Done + Frame("{\"future\":false}"),
            "choice-limit" => Start + Frame("{\"choices\":[{\"index\":1,\"text\":\"other\"}]}") + Done,
            "tail-limit" => Start + Finish + Frame("{\"choices\":[],\"usage\":{\"total_tokens\":0}}") + Done,
            "fake-filter-null" => Start + Finish + Frame("{\"choices\":[{\"index\":0,\"finish_reason\":\"content_filter\",\"content_filter_results\":null}]}") + Done,
            "fake-filter-bool" => Start + Finish + Frame("{\"choices\":[{\"index\":0,\"finish_reason\":\"content_filter\",\"content_filter_results\":false}]}") + Done,
            "wrong-object" => Start + Frame("{\"object\":\"chat.completion.chunk\",\"choices\":[]}") + Done,
            _ => Start + Frame("{\"choices\":[{\"index\":0,\"logprobs\":[]}]}") + Done
        };
        using var stream = new AzureCompactionReadStream(Encoding.UTF8.GetBytes(raw)); using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(AzureTextFixture.Streaming(stream));
        var provider = new AzureLlmProvider(factory, new() { MaximumTrackedChoices = kind == "choice-limit" ? 1 : 128, MaximumBufferedTerminalEvents = kind == "tail-limit" ? 1 : 128 }); var seen = new List<TextCompletionEvent>();
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in ((ITextCompletionsAdapter)provider).StreamAsync(AzureTextFixture.Payload(true), AzureTextFixture.Context(), TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.True(stream.Disposed); Assert.Equal(1, factory.Disposals); Assert.Equal(1, factory.Calls); Assert.DoesNotContain(seen, item => item.Data.TryGetProperty("choices", out var choices) && choices.EnumerateArray().Any(c => c.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != System.Text.Json.JsonValueKind.Null));
    }
    /// <summary>正常 DONE 后的 EOF/读取/异步释放/客户端释放/最终取消错误都不能交付暂存终态。</summary>
    [Theory] [InlineData("read")] [InlineData("dispose")] [InlineData("client")] [InlineData("cancel")]
    public async Task TailWaitsForAllOwnedResources(string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); using var stream = new AzureCompactionReadStream(Encoding.UTF8.GetBytes(AzureTextFixture.Sse())) { FailRead = kind == "read", FailDispose = kind == "dispose" }; using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(AzureTextFixture.Streaming(stream));
        if (kind == "client") factory.OnDisposeClient = () => throw new IOException("fixture client disposal failure"); if (kind == "cancel") factory.OnDisposeClient = cancellation.Cancel; var seen = new List<TextCompletionEvent>();
        var failure = await Record.ExceptionAsync(async () => { await foreach (var item in ((ITextCompletionsAdapter)new AzureLlmProvider(factory)).StreamAsync(AzureTextFixture.Payload(true), AzureTextFixture.Context(), cancellation.Token)) seen.Add(item); });
        Assert.NotNull(failure); if (kind == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure); Assert.Equal(3, seen.Count); Assert.True(stream.Disposed); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }
    /// <summary>原始总字节与长度边界覆盖注释/SSE 元数据，完整字节上限允许正常完成。</summary>
    [Theory] [InlineData("exact")] [InlineData("limit")] [InlineData("short")] [InlineData("long")]
    public async Task RawSseLengthAndTotalByteBound(string kind)
    {
        var bytes = Encoding.UTF8.GetBytes(AzureTextFixture.Sse()); using var stream = new AzureCompactionReadStream(bytes); using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) => { var reply = AzureTextFixture.Streaming(stream); if (kind is "short" or "long") reply.Content.Headers.ContentLength = bytes.Length + (kind == "short" ? -1 : 1); return Task.FromResult(reply); };
        var provider = new AzureLlmProvider(factory, new() { MaximumBodyBytes = bytes.Length - (kind == "limit" ? 1 : 0) }); var sequence = ((ITextCompletionsAdapter)provider).StreamAsync(AzureTextFixture.Payload(true), AzureTextFixture.Context(), TestContext.Current.CancellationToken);
        if (kind == "exact") Assert.Equal(9, (await AzureTextFixture.Drain(sequence, TestContext.Current.CancellationToken)).Count); else await Assert.ThrowsAsync<InvalidDataException>(() => AzureTextFixture.Drain(sequence, TestContext.Current.CancellationToken)); Assert.True(stream.Disposed); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>原生错误保留 data JSON 字节与 HTTP 状态，不将前面的 finish 转成成功。</summary>
    [Fact]
    public async Task NativeErrorAfterFinishRetainsDataAndSuppressesTail()
    {
        var raw = AzureTextFixture.Part("native_error").GetRawText(); using var stream = new AzureCompactionReadStream(Encoding.UTF8.GetBytes(Start + Finish + Frame(raw))); using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(AzureTextFixture.Streaming(stream)); var seen = 0;
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { await foreach (var _ in ((ITextCompletionsAdapter)new AzureLlmProvider(factory)).StreamAsync(AzureTextFixture.Payload(true), AzureTextFixture.Context(), TestContext.Current.CancellationToken)) seen++; }); Assert.Equal(1, seen); Assert.Equal(Encoding.UTF8.GetBytes(raw), error.Body.ToArray()); Assert.Equal(System.Net.HttpStatusCode.OK, error.StatusCode); Assert.Equal("text/event-stream; charset=utf-8", error.ContentType); Assert.Equal(1, factory.Calls);
    }
    /// <summary>没有数据和 choice 的 DONE 不能替代缺失的原生结束原因。</summary>
    [Fact]
    public async Task BareDoneCannotBecomeSuccessfulCompletion()
    { using var stream = new AzureCompactionReadStream(Encoding.UTF8.GetBytes(Done)); using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(AzureTextFixture.Streaming(stream)); await Assert.ThrowsAsync<InvalidDataException>(() => AzureTextFixture.Drain(((ITextCompletionsAdapter)new AzureLlmProvider(factory)).StreamAsync(AzureTextFixture.Payload(true), AzureTextFixture.Context(), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)); }
    /// <summary>没有明确错误事件名的未知 code/message 事件保持原文，不能被当作 HTTP 平铺错误。</summary>
    [Fact]
    public async Task UnknownSseCodeAndMessageAreRetained()
    {
        const string raw = "{\"code\":\"business-code\",\"message\":\"business-message\",\"future\":9007199254740993}"; using var stream = new AzureCompactionReadStream(Encoding.UTF8.GetBytes(Start + Finish + Frame(raw) + Done)); using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(AzureTextFixture.Streaming(stream));
        var events = await AzureTextFixture.Drain(((ITextCompletionsAdapter)new AzureLlmProvider(factory)).StreamAsync(AzureTextFixture.Payload(true), AzureTextFixture.Context(), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken); Assert.Equal(3, events.Count); Assert.Equal(raw, events[2].Data.GetRawText()); Assert.True(stream.Eof); Assert.True(stream.Disposed);
    }
    /// <summary>明确 error SSE 事件中的文档化平铺错误保持原始 JSON 和状态。</summary>
    [Fact]
    public async Task ExplicitFlatErrorEventRemainsNativeError()
    {
        const string raw = "{\"code\":\"native-code\",\"message\":\"native failure\",\"future\":false}"; using var stream = new AzureCompactionReadStream(Encoding.UTF8.GetBytes(Start + Finish + "event: error\n" + Frame(raw))); using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(AzureTextFixture.Streaming(stream));
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => AzureTextFixture.Drain(((ITextCompletionsAdapter)new AzureLlmProvider(factory)).StreamAsync(AzureTextFixture.Payload(true), AzureTextFixture.Context(), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)); Assert.Equal(raw, error.ResponseBody); Assert.Equal(1, factory.Calls);
    }

    private static string Frame(string raw) => "data: " + raw + "\n\n";
}
