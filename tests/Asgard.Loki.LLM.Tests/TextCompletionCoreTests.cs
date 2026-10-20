using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.LLM.Tests;

/// <summary>新增原生协议沿用完整目标/Session 绑定与原生快照，不管理 prompt 历史或转换消息。</summary>
public sealed class TextCompletionCoreTests
{
    /// <summary>公共入口只映射固定实际模型，原生 prompt、未知数值和返回模型不被重写。</summary>
    [Fact]
    public async Task PublicProtocolRetainsOriginalPayloadAndMetadata()
    {
        using var provider = new TextCompletionStubProvider(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build();
        var request = Request(); var before = JsonSerializer.Serialize(request); var result = await client.LLM.TextCompletions.CompleteAsync(request, new() { SessionId = "native" }, TestContext.Current.CancellationToken);
        var call = Assert.Single(provider.Calls); Assert.Equal("actual-a", call.Body["model"]!.GetValue<string>()); Assert.False(call.Body["stream"]!.GetValue<bool>());
        Assert.Equal(" 原文 😀 ", call.Body["prompt"]!.GetValue<string>()); Assert.Equal("9007199254740993", call.Body["future"]!["exact"]!.ToJsonString());
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.Equal(new LlmResultMetadata("public", "actual-a", provider.Id, "a"), result.Metadata);
        Assert.Equal("native-reported", result.Data.GetProperty("model").GetString()); Assert.Equal("9007199254740993", result.Data.GetProperty("future").GetRawText());
    }
    /// <summary>同一 Session 保持服务器/模型/凭据绑定，新 Session 才选择其他完整目标。</summary>
    [Fact]
    public async Task SessionKeepsWholeTargetAndNewSessionsRotate()
    {
        using var provider = new TextCompletionStubProvider(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build();
        foreach (var session in new[] { "bound", "bound", "other" }) await client.LLM.TextCompletions.CompleteAsync(Request(), new() { SessionId = session }, TestContext.Current.CancellationToken);
        var calls = provider.Calls.ToArray(); Assert.Equal(new[] { "a", "a", "b" }, calls.Select(c => c.Context.Target.Id));
        Assert.Equal("https://a.example.invalid/v1/", calls[0].Context.Target.BaseAddress.AbsoluteUri); Assert.Equal("fixture-key-a", calls[0].Context.Credential.ApiKey);
        Assert.Equal("https://b.example.invalid/v1/", calls[2].Context.Target.BaseAddress.AbsoluteUri); Assert.Equal("fixture-key-b", calls[2].Context.Credential.ApiKey); Assert.Equal("actual-b", calls[2].Body["model"]!.GetValue<string>());
    }
    /// <summary>首次目标选择期间变更原字典不会改变已冻结的原生请求。</summary>
    [Fact]
    public async Task PayloadSnapshotPrecedesTargetSelection()
    {
        var request = Request(); var selector = new ModerationTestSelector(request.Parameters.Clear); using var provider = new TextCompletionStubProvider();
        await using var client = new LokiClientBuilder().AddLLM(Configuration(provider) with { TargetSelector = selector }).Build();
        await client.LLM.TextCompletions.CompleteAsync(request, new() { SessionId = "snapshot" }, TestContext.Current.CancellationToken);
        Assert.Empty(request.Parameters); Assert.Equal(" 原文 😀 ", Assert.Single(provider.Calls).Body["prompt"]!.GetValue<string>()); Assert.Equal(1, selector.Calls);
    }
    /// <summary>任何完整绑定成员漂移都失败，不静默选其他目标或替换旧适配器。</summary>
    [Theory] [InlineData("server")] [InlineData("model")] [InlineData("credential")] [InlineData("adapter")] [InlineData("version")] [InlineData("protocol")] [InlineData("removed")]
    public async Task BindingDriftCannotSwitchToAnotherTarget(string change)
    {
        using var provider = new TextCompletionStubProvider(); var configuration = Configuration(provider);
        await using (var first = new LokiClientBuilder().AddLLM(configuration).Build()) await first.LLM.TextCompletions.CompleteAsync(Request(), new() { SessionId = "drift" }, TestContext.Current.CancellationToken);
        var targets = configuration.Routes[0].Targets.ToArray();
        targets[0] = change switch
        {
            "server" => targets[0] with { BaseAddress = new("https://changed.example.invalid/v1/") },
            "model" => targets[0] with { UpstreamModel = "changed-model" },
            "version" => targets[0] with { Version = "2" },
            "protocol" => targets[0] with { Protocols = LlmProtocol.TextCompletions | LlmProtocol.ChatCompletions },
            _ => targets[0]
        };
        if (change == "removed") targets = [targets[1]];
        using var changedProvider = new TextCompletionStubProvider("text-stub-v2");
        configuration = configuration with { Routes = [new() { Model = "public", Targets = targets }] };
        if (change == "credential") configuration = configuration with { Credentials = [new("key-a", "changed-fixture-key"), configuration.Credentials[1]] };
        if (change == "adapter") configuration = configuration with { Providers = [changedProvider] };
        await using var second = new LokiClientBuilder().AddLLM(configuration).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.TextCompletions.CompleteAsync(Request(), new() { SessionId = "drift" }, TestContext.Current.CancellationToken));
        Assert.Single(provider.Calls); Assert.Empty(changedProvider.Calls);
    }
    /// <summary>同一目标声明两种协议也不能在已有 Session 中切换协议。</summary>
    [Fact]
    public async Task ExistingChatSessionCannotBecomeTextCompletionSession()
    {
        using var provider = new TextCompletionStubProvider(); var configuration = Configuration(provider);
        configuration = configuration with { Routes = [new() { Model = "public", Targets = configuration.Routes[0].Targets.Select(t => t with { Protocols = LlmProtocol.ChatCompletions | LlmProtocol.TextCompletions }).ToArray() }] };
        await using var client = new LokiClientBuilder().AddLLM(configuration).Build(); var options = new LlmCallOptions { SessionId = "chat-bound" };
        await client.LLM.ChatCompletions.CompleteAsync(new() { Model = "public", Messages = [] }, options, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LLM.TextCompletions.CompleteAsync(Request(), options, TestContext.Current.CancellationToken)); Assert.Empty(provider.Calls); Assert.Equal(1, provider.ChatCalls);
    }
    /// <summary>保留已公开的协议数值，缓存绑定不能因插入新枚举值而改变含义。</summary>
    [Fact]
    public void ExistingProtocolValuesRemainStable()
    {
        Assert.Equal(1, (int)LlmProtocol.ChatCompletions); Assert.Equal(2, (int)LlmProtocol.Responses); Assert.Equal(4, (int)LlmProtocol.ClaudeMessages); Assert.Equal(8, (int)LlmProtocol.Gemini);
        Assert.Equal(16, (int)LlmProtocol.OllamaChat); Assert.Equal(32, (int)LlmProtocol.OllamaGenerate); Assert.Equal(64, (int)LlmProtocol.BedrockConverse); Assert.Equal(128, (int)LlmProtocol.BedrockNova); Assert.Equal(256, (int)LlmProtocol.Realtime); Assert.Equal(512, (int)LlmProtocol.Moderations); Assert.Equal(1024, (int)LlmProtocol.TextCompletions);
    }
    /// <summary>模型覆盖、流式控制与 Undefined 在选择目标或调用供应商之前失败。</summary>
    [Theory] [InlineData("model")] [InlineData("MODEL")] [InlineData("stream")] [InlineData("STREAM")] [InlineData("undefined")]
    public async Task ReservedOrUndefinedParametersCannotReachProvider(string field)
    {
        using var provider = new TextCompletionStubProvider(); var selector = new ModerationTestSelector(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider) with { TargetSelector = selector }).Build();
        var request = Request(); request.Parameters[field] = field == "undefined" ? default : JsonSerializer.SerializeToElement(false);
        await Assert.ThrowsAsync<ArgumentException>(() => client.LLM.TextCompletions.CompleteAsync(request, new() { SessionId = "invalid" }, TestContext.Current.CancellationToken)); Assert.Empty(provider.Calls); Assert.Equal(0, selector.Calls);
    }
    /// <summary>供应商失败保持原异常且不重试或失效转移，模块不释放借用的供应商。</summary>
    [Fact]
    public async Task ProviderFailureIsSingleAttemptAndRemainsBorrowed()
    {
        var failure = new IOException("fixture native failure"); using var provider = new TextCompletionStubProvider { Failure = failure };
        await using (var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build())
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => client.LLM.TextCompletions.CompleteAsync(Request(), new() { SessionId = "failed" }, TestContext.Current.CancellationToken)));
        Assert.Single(provider.Calls); Assert.False(provider.Disposed);
    }
    /// <summary>预先取消不会选目标或创建绑定。</summary>
    [Fact]
    public async Task PreCancelledCallDoesNotSelectTarget()
    {
        using var provider = new TextCompletionStubProvider(); var selector = new ModerationTestSelector(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider) with { TargetSelector = selector }).Build();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.LLM.TextCompletions.CompleteAsync(Request(), new() { SessionId = "cancelled" }, cancellation.Token)); Assert.Equal(0, selector.Calls); Assert.Empty(provider.Calls);
    }
    internal static TextCompletionRequest Request() => new()
    {
        Model = "public", Parameters = new() { ["prompt"] = JsonSerializer.SerializeToElement(" 原文 😀 "), ["future"] = JsonSerializer.SerializeToElement(new { exact = 9007199254740993L, nullable = (object?)null, flag = false }) }
    };
    internal static LlmOptions Configuration(ILlmProvider provider) => new()
    {
        Providers = [provider], Credentials = [new("key-a", "fixture-key-a"), new("key-b", "fixture-key-b")],
        SessionStore = new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock()),
        Routes = [new() { Model = "public", Targets = [Target("a", provider.Id), Target("b", provider.Id)] }]
    };
    private static LlmTarget Target(string id, string provider) => new()
    { Id = id, ProviderId = provider, BaseAddress = new("https://" + id + ".example.invalid/v1/"), UpstreamModel = "actual-" + id, CredentialId = "key-" + id, Protocols = LlmProtocol.TextCompletions };
}
