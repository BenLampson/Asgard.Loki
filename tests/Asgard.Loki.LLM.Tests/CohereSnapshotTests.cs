using System.Text.Json;
using Xunit;
using static Asgard.Loki.LLM.Tests.CohereCoreTests;

namespace Asgard.Loki.LLM.Tests;

/// <summary>首次缓存等待、非法请求与最终取消边界，完全离线且不执行工具。</summary>
public sealed class CohereSnapshotTests
{
    /// <summary>完整与流式请求都在首次异步缓存等待前冻结全部原生字段。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Snapshot_PrecedesFirstCacheWait(bool stream)
    {
        using var provider = new CohereStubProvider(); var options = Configuration(provider);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ControlledSessionStore(options.SessionStore!) { BeforeRead = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        await using var client = new LokiClientBuilder().AddLLM(options with { SessionStore = store }).Build(); var request = Request(); var token = TestContext.Current.CancellationToken;
        async Task Invoke() { if (stream) await foreach (var _ in client.LLM.CohereChat.StreamAsync(request, new() { SessionId = "waiting" }, token)) { } else await client.LLM.CohereChat.CompleteAsync(request, new() { SessionId = "waiting" }, token); }
        var pending = Invoke(); await entered.Task.WaitAsync(token); request.Parameters.Clear(); release.SetResult(); await pending;
        var call = Assert.Single(provider.Calls); Assert.Equal("上海与北京的天气？", call.Body["message"]!.GetValue<string>());
        Assert.Equal(4, call.Body["chat_history"]!.AsArray().Count); Assert.Equal(2, call.Body["tool_results"]!.AsArray().Count); Assert.Equal(stream, call.Body["stream"]!.GetValue<bool>());
    }
    /// <summary>保留字段、Undefined 和任何原始 Unicode 错误均在缓存与选择器之前拒绝。</summary>
    [Theory]
    [InlineData(false, "model")] [InlineData(true, "model")] [InlineData(false, "MODEL")] [InlineData(true, "MODEL")]
    [InlineData(false, "stream")] [InlineData(true, "stream")] [InlineData(false, "STREAM")] [InlineData(true, "STREAM")]
    [InlineData(false, "undefined")] [InlineData(true, "undefined")] [InlineData(false, "unicode-model")] [InlineData(true, "unicode-model")]
    [InlineData(false, "unicode-value")] [InlineData(true, "unicode-value")] [InlineData(false, "unicode-key")] [InlineData(true, "unicode-key")]
    public async Task InvalidRequest_FailsBeforeCache(bool stream, string kind)
    {
        using var provider = new CohereStubProvider(); var options = Configuration(provider); var reads = 0;
        var store = new ControlledSessionStore(options.SessionStore!) { BeforeRead = _ => { reads++; return Task.CompletedTask; } }; var selector = new ModerationTestSelector();
        await using var client = new LokiClientBuilder().AddLLM(options with { SessionStore = store, TargetSelector = selector }).Build(); var request = Request();
        if (kind == "unicode-model") request = request with { Model = new string((char)0xd800, 1) };
        else if (kind == "unicode-value") { using var document = JsonDocument.Parse("{\"unknown\":\"\\ud800\"}"); request.Parameters["future"] = document.RootElement.Clone(); }
        else if (kind == "unicode-key") { using var document = JsonDocument.Parse("{\"\\ud800\":null}"); request.Parameters["future"] = document.RootElement.Clone(); }
        else request.Parameters[kind] = kind == "undefined" ? default : JsonSerializer.SerializeToElement(false);
        async Task Invoke() { if (stream) await foreach (var _ in client.LLM.CohereChat.StreamAsync(request, new() { SessionId = "invalid" }, TestContext.Current.CancellationToken)) { } else await client.LLM.CohereChat.CompleteAsync(request, new() { SessionId = "invalid" }, TestContext.Current.CancellationToken); }
        Assert.NotNull(await Record.ExceptionAsync(Invoke)); Assert.Empty(provider.Calls); Assert.Equal(0, reads); Assert.Equal(0, store.GetOrCreateCalls); Assert.Equal(0, selector.Calls);
    }
    /// <summary>供应商仍可明确保留参数缺省、null 和未知结构，核心不添加生成默认值。</summary>
    [Theory] [InlineData("absent")] [InlineData("null")] [InlineData("false")] [InlineData("0")] [InlineData("[]")]
    public async Task NativeExtensionShapes_ArePreserved(string json)
    {
        using var provider = new CohereStubProvider(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build(); var request = Request();
        request.Parameters.Remove("future"); if (json != "absent") request.Parameters["future"] = JsonSerializer.Deserialize<JsonElement>(json);
        await client.LLM.CohereChat.CompleteAsync(request, new() { SessionId = "shape" }, TestContext.Current.CancellationToken);
        var body = Assert.Single(provider.Calls).Body; Assert.Equal(json != "absent", body.ContainsKey("future"));
        if (json != "absent") Assert.Equal(json, body["future"]?.ToJsonString() ?? "null");
    }
    /// <summary>预取消和供应商结果时取消都阻止交付，不自动切换目标。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Cancellation_PreventsDelivery(bool stream, bool preCancelled)
    {
        using var cancel = new CancellationTokenSource(); using var provider = new CohereStubProvider { OnResult = cancel.Cancel };
        await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build(); if (preCancelled) cancel.Cancel();
        async Task Invoke() { if (stream) await foreach (var _ in client.LLM.CohereChat.StreamAsync(Request(), new() { SessionId = "cancel" }, cancel.Token)) Assert.Fail("Cancelled event published"); else await client.LLM.CohereChat.CompleteAsync(Request(), new() { SessionId = "cancel" }, cancel.Token); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(Invoke); Assert.Equal(preCancelled ? 0 : 1, provider.Calls.Count);
    }
    /// <summary>原始失败原样传播且不重试；模块不释放借用适配器。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Failure_IsSingleAttemptAndBorrowedProviderSurvives(bool stream)
    {
        var failure = new IOException("fixture"); using var provider = new CohereStubProvider { Failure = failure };
        await using (var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build())
        {
            async Task Invoke() { if (stream) await foreach (var _ in client.LLM.CohereChat.StreamAsync(Request(), new() { SessionId = "failure" }, TestContext.Current.CancellationToken)) { } else await client.LLM.CohereChat.CompleteAsync(Request(), new() { SessionId = "failure" }, TestContext.Current.CancellationToken); }
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(Invoke));
        }
        Assert.Single(provider.Calls); Assert.False(provider.Disposed);
    }
    /// <summary>已有增量之后出现原始读取错误也不重放请求。</summary>
    [Fact]
    public async Task StreamTailFailure_DoesNotRetry()
    {
        var failure = new IOException("late fixture"); using var provider = new CohereStubProvider { AfterFirstEvent = failure };
        await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build(); var count = 0;
        var actual = await Assert.ThrowsAsync<IOException>(async () => { await foreach (var _ in client.LLM.CohereChat.StreamAsync(Request(), new() { SessionId = "tail" }, TestContext.Current.CancellationToken)) count++; });
        Assert.Same(failure, actual); Assert.Equal(1, count); Assert.Single(provider.Calls);
    }
    /// <summary>一个已经绑定其他协议的 Session 不能静默转为 Cohere。</summary>
    [Fact]
    public async Task ExistingSession_CannotSwitchProtocol()
    {
        using var provider = new CohereStubProvider(); var options = Configuration(provider);
        options = options with { Routes = [new() { Model = "public-cohere", Targets = options.Routes[0].Targets.Select(t => t with { Protocols = LlmProtocol.CohereChat | LlmProtocol.ChatCompletions }).ToArray() }] };
        await using var client = new LokiClientBuilder().AddLLM(options).Build(); var call = new LlmCallOptions { SessionId = "bound" };
        await client.LLM.ChatCompletions.CompleteAsync(new() { Model = "public-cohere", Messages = [] }, call, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LLM.CohereChat.CompleteAsync(Request(), call, TestContext.Current.CancellationToken)); Assert.Empty(provider.Calls);
    }
}
