using System.Text.Json;
using Xunit;
using static Asgard.Loki.LLM.Tests.TextCompletionCoreTests;

namespace Asgard.Loki.LLM.Tests;

/// <summary>文本补全流式入口、原生 prompt 边界和首次异步缓存前快照，完全离线。</summary>
public sealed class TextCompletionStreamCoreTests
{
    /// <summary>流式方法独占 stream 标志，完整原生事件和 SSE 元数据不被修改。</summary>
    [Fact]
    public async Task StreamUsesNativeProtocolAndRetainsSseMetadata()
    {
        using var provider = new TextCompletionStubProvider(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build(); var request = Request(); var before = JsonSerializer.Serialize(request); List<TextCompletionEvent> events = [];
        await foreach (var item in client.LLM.TextCompletions.StreamAsync(request, new() { SessionId = "stream" }, TestContext.Current.CancellationToken)) events.Add(item);
        var call = Assert.Single(provider.Calls); Assert.True(call.Body["stream"]!.GetValue<bool>()); Assert.Equal("actual-a", call.Body["model"]!.GetValue<string>()); Assert.Equal(before, JsonSerializer.Serialize(request));
        var result = Assert.Single(events); Assert.Equal("native.text", result.EventName); Assert.Equal("event-original", result.EventId); Assert.Equal(750, result.RetryMilliseconds); Assert.Equal("9007199254740993", result.Data.GetProperty("future").GetRawText()); Assert.Equal("native-reported", result.Data.GetProperty("model").GetString());
    }
    /// <summary>prompt 的省略/null/文本/文本批次/token/token 批次在核心原样传递，不套模板。</summary>
    [Theory] [InlineData("absent")] [InlineData("null")] [InlineData("\"\"")] [InlineData("[\"a\",\" b \"]")]
    [InlineData("[42,0]")] [InlineData("[[42,0],[7]]")]
    public async Task NativePromptShapeIsNotInterpretedByCore(string json)
    {
        using var provider = new TextCompletionStubProvider(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build(); var request = Request(); request.Parameters.Remove("prompt"); if (json != "absent") request.Parameters.Add("prompt", JsonSerializer.Deserialize<JsonElement>(json));
        await client.LLM.TextCompletions.CompleteAsync(request, new() { SessionId = "shape" }, TestContext.Current.CancellationToken); var body = Assert.Single(provider.Calls).Body; Assert.Equal(json != "absent", body.ContainsKey("prompt"));
        if (json != "absent") Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(json), JsonSerializer.SerializeToElement(body["prompt"])));
    }
    /// <summary>完整/流式请求均在首次真正等待缓存前冻结全部参数。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SnapshotPrecedesAsynchronousCacheRead(bool stream)
    {
        using var provider = new TextCompletionStubProvider(); var options = Configuration(provider); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ControlledSessionStore(options.SessionStore!) { BeforeRead = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        await using var client = new LokiClientBuilder().AddLLM(options with { SessionStore = store }).Build(); var request = Request(); var token = TestContext.Current.CancellationToken;
        async Task Invoke() { if (stream) await foreach (var _ in client.LLM.TextCompletions.StreamAsync(request, new() { SessionId = "cache-wait" }, token)) { } else await client.LLM.TextCompletions.CompleteAsync(request, new() { SessionId = "cache-wait" }, token); }
        var pending = Invoke(); await entered.Task.WaitAsync(token); request.Parameters.Clear(); release.SetResult(); await pending;
        var call = Assert.Single(provider.Calls); Assert.Equal(" 原文 😀 ", call.Body["prompt"]!.GetValue<string>()); Assert.Equal("9007199254740993", call.Body["future"]!["exact"]!.ToJsonString()); Assert.Equal(stream, call.Body["stream"]!.GetValue<bool>()); Assert.Equal(1, store.GetOrCreateCalls);
    }
    /// <summary>无效扩展与未知字段中的非法原始 Unicode 在缓存和选择器之前拒绝。</summary>
    [Theory] [InlineData("MODEL")] [InlineData("STREAM")] [InlineData("undefined")] [InlineData("unicode")]
    public async Task InvalidStreamRequestNeverSelectsTarget(string kind)
    {
        using var provider = new TextCompletionStubProvider(); var selector = new ModerationTestSelector(); var options = Configuration(provider); var store = new ControlledSessionStore(options.SessionStore!); await using var client = new LokiClientBuilder().AddLLM(options with { SessionStore = store, TargetSelector = selector }).Build(); var request = Request();
        if (kind == "unicode") request = request with { Model = "\ud800" }; else request.Parameters[kind] = kind == "undefined" ? default : JsonSerializer.SerializeToElement(false);
        var error = await Record.ExceptionAsync(async () => { await foreach (var _ in client.LLM.TextCompletions.StreamAsync(request, new() { SessionId = "invalid" }, TestContext.Current.CancellationToken)) { } }); Assert.NotNull(error); if (kind == "unicode") Assert.IsType<System.Text.EncoderFallbackException>(error); else Assert.IsType<ArgumentException>(error);
        Assert.Equal(0, store.GetOrCreateCalls); Assert.Equal(0, selector.Calls); Assert.Empty(provider.Calls);
    }
    /// <summary>适配器忽略最后取消时核心也不能交付完整结果或下一条流事件。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FinalCancellationPreventsPublicDelivery(bool stream)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); using var provider = new TextCompletionStubProvider { OnResult = cancellation.Cancel }; await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build();
        async Task Invoke() { if (stream) await foreach (var _ in client.LLM.TextCompletions.StreamAsync(Request(), new() { SessionId = "cancel" }, cancellation.Token)) Assert.Fail("Cannot publish cancelled event"); else await client.LLM.TextCompletions.CompleteAsync(Request(), new() { SessionId = "cancel" }, cancellation.Token); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(Invoke); Assert.Single(provider.Calls);
    }
    /// <summary>已有增量之后的适配器失败仍向调用者传播同一异常，不另选目标或重放。</summary>
    [Fact]
    public async Task FailureAfterFirstEventDoesNotRetry()
    {
        var failure = new IOException("fixture stream tail failure"); using var provider = new TextCompletionStubProvider { AfterFirstEvent = failure }; await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build(); var events = 0;
        var actual = await Assert.ThrowsAsync<IOException>(async () => { await foreach (var _ in client.LLM.TextCompletions.StreamAsync(Request(), new() { SessionId = "tail-fail" }, TestContext.Current.CancellationToken)) events++; }); Assert.Same(failure, actual); Assert.Equal(1, events); Assert.Single(provider.Calls);
    }
    /// <summary>文本补全结果 ID 不进入 Responses 的延续归属索引。</summary>
    [Fact]
    public async Task TextCompletionIdsAreNotResponseOwnership()
    {
        using var provider = new TextCompletionStubProvider(); var options = Configuration(provider); await using var client = new LokiClientBuilder().AddLLM(options).Build(); var result = await client.LLM.TextCompletions.CompleteAsync(Request(), new() { SessionId = "ownership" }, TestContext.Current.CancellationToken);
        var binding = await options.SessionStore!.GetAsync("ownership", TestContext.Current.CancellationToken); Assert.NotNull(binding); Assert.Equal(LlmProtocol.TextCompletions, binding.Protocol); Assert.False(await options.SessionStore.OwnsResponseAsync(binding, "cmpl-fixture", TestContext.Current.CancellationToken)); Assert.Equal("cmpl-fixture", result.Data.GetProperty("id").GetString());
    }
}
