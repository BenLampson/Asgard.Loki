using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asgard.Loki.LLM.Tests;

/// <summary>新协议只增加原生契约和显式路由，不将供应商依赖带入核心。</summary>
public sealed class BedrockNovaContractTests
{
    /// <summary>Nova 原生信封类型与外层传输名独立，未知 type 字段不改变语义。</summary>
    [Fact]
    public void NativeNovaType_ComesFromDecodedEnvelopeIncludingBaseView()
    {
        var data = JsonSerializer.SerializeToElement(new { type = "error", future = false }); var metadata = new LlmResultMetadata("public", "actual", "provider", "target"); LlmJsonEvent native = new BedrockNovaEvent(data, metadata, "chunk", "metadata"); Assert.Equal("metadata", native.Type); Assert.Equal("chunk", native.EventName); Assert.Equal("error", native.Data.GetProperty("type").GetString()); Assert.Equal("error", new ClaudeMessageEvent(data, metadata, "chunk").Type);
    }
    private static ControlledSessionStore Store() => new(new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock()));
    private static ILokiClient Client(ILlmProvider provider, ILlmSessionStore? store = null, LlmProtocol protocols = LlmProtocol.BedrockNova) => new LokiClientBuilder().AddLLM(new() { Providers = [provider], Credentials = [new("credential", "fixture-key")], SessionStore = store ?? Store(), Routes = [new() { Model = "public", Targets = [new() { Id = "target", ProviderId = provider.Id, BaseAddress = new("https://first.invalid/runtime/"), UpstreamModel = "actual-model", CredentialId = "credential", Protocols = protocols }] }] }).Build();
    private static BedrockNovaRequest Request() => JsonSerializer.Deserialize<BedrockNovaRequest>("""{"model":"public","messages":null,"inferenceConfig":{"maxTokens":0,"temperature":0},"stream":"native-future","native":{"integer":9007199254740993,"false":false,"nil":null,"empty":[]}}""")!;
    /// <summary>完整/流式都只替换实际 model；原生可选 null、零值、未知字段均保留。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeContract_RetainsExactJsonAndMetadata(bool stream)
    {
        var provider = new StubBedrockNovaProvider(); await using var client = Client(provider); var input = Request(); var original = JsonSerializer.Serialize(input);
        if (stream) { var events = new List<BedrockNovaEvent>(); await foreach (var item in client.LLM.BedrockNova.StreamAsync(input, new() { SessionId = "session" }, TestContext.Current.CancellationToken)) events.Add(item); Assert.Equal(2, events.Count); Assert.Equal("contentBlockDelta", events[0].Type); Assert.Equal(7, events[0].Data.GetProperty("contentBlockDelta").GetProperty("contentBlockIndex").GetInt32()); Assert.Null(events[0].EventId); Assert.Null(events[0].RetryMilliseconds); }
        else { var result = await client.LLM.BedrockNova.InvokeAsync(input, new() { SessionId = "session" }, TestContext.Current.CancellationToken); Assert.Equal("end_turn", result.Data.GetProperty("stopReason").GetString()); Assert.Equal(JsonValueKind.Null, result.Data.GetProperty("future").ValueKind); }
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"model":"actual-model","messages":null,"inferenceConfig":{"maxTokens":0,"temperature":0},"stream":"native-future","native":{"integer":9007199254740993,"false":false,"nil":null,"empty":[]}}"""), provider.LastRequest)); Assert.Equal(original, JsonSerializer.Serialize(input)); Assert.Equal("actual-model", provider.LastContext!.Metadata.UpstreamModel); Assert.Equal(1, provider.Calls);
    }
    /// <summary>原生正文在缓存等待前拍摄快照，不受调用方后续修改影响。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Snapshot_PrecedesSessionAwait(bool stream)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var store = Store(); store.BeforeRead = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); }; var provider = new StubBedrockNovaProvider(); await using var client = Client(provider, store); var input = Request(); async Task Invoke() { if (stream) { await foreach (var _ in client.LLM.BedrockNova.StreamAsync(input, new() { SessionId = "session" }, TestContext.Current.CancellationToken)) { } } else await client.LLM.BedrockNova.InvokeAsync(input, new() { SessionId = "session" }, TestContext.Current.CancellationToken); } var call = Invoke(); await entered.Task.WaitAsync(TestContext.Current.CancellationToken); input.Parameters.Clear(); release.SetResult(); await call; Assert.True(provider.LastRequest!.ContainsKey("messages")); Assert.True(provider.LastRequest.ContainsKey("native"));
    }
    /// <summary>保留路由字段、非法原始字符和无效参数集合在建立 Session 前拒绝。</summary>
    [Theory] [InlineData("reserved")] [InlineData("unicode")] [InlineData("null-parameters")]
    public async Task InvalidInput_FailsBeforeAdapter(string scenario)
    {
        var provider = new StubBedrockNovaProvider(); var store = Store(); await using var client = Client(provider, store); var input = Request(); if (scenario == "reserved") input.Parameters["model"] = JsonSerializer.SerializeToElement("other"); if (scenario == "unicode") input.Parameters[new string((char)0xd800, 1)] = JsonSerializer.SerializeToElement(true); if (scenario == "null-parameters") input = input with { Parameters = null! }; var error = await Record.ExceptionAsync(() => client.LLM.BedrockNova.InvokeAsync(input, new() { SessionId = "session" }, TestContext.Current.CancellationToken)); Assert.NotNull(error); Assert.Equal(0, provider.Calls); Assert.Equal(0, store.GetOrCreateCalls);
    }
    /// <summary>显式协议必须同时有对应适配器；其他协议不能因 Nova 适配器而被声明。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void MismatchedRegistration_IsRejected(bool converseOnly)
    {
        if (converseOnly) Assert.Throws<ArgumentException>(() => Client(new StubBedrockNovaProvider(), protocols: LlmProtocol.ClaudeMessages)); else Assert.Throws<ArgumentException>(() => Client(new StubLlmProvider()));
    }
    /// <summary>未声明原生协议的目标不被自动改写或转换。</summary>
    [Fact]
    public async Task UnsupportedRoute_DoesNotInvokeOtherProtocol()
    {
        var provider = new StubLlmProvider(); await using var client = Client(provider, protocols: LlmProtocol.ChatCompletions); await Assert.ThrowsAsync<NotSupportedException>(() => client.LLM.BedrockNova.InvokeAsync(Request(), new() { SessionId = "session" }, TestContext.Current.CancellationToken)); Assert.Empty(provider.Calls);
    }
    /// <summary>完整调用的适配器忽略取消也不能让取消后的结果作为成功返回。</summary>
    [Fact]
    public async Task FinalCancellation_WithholdsResult()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var provider = new StubBedrockNovaProvider { OnComplete = cancel.Cancel }; await using var client = Client(provider); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.LLM.BedrockNova.InvokeAsync(Request(), new() { SessionId = "session" }, cancel.Token)); Assert.Equal(1, provider.Calls);
    }
}
