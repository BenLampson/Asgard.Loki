using Asgard.Loki.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Tests;

/// <summary>通过内存缓存验证完整目标与响应链保持。</summary>
public sealed class LlmSessionTests
{
    private readonly MemoryDistributedCache _cache = new(Options.Create(new MemoryDistributedCacheOptions()));
    private readonly TestSessionLock _sessionLock = new();
    private readonly StubLlmProvider _provider = new();

    /// <summary>不同服务器与实际模型都可以映射到同一个公共模型。</summary>
    [Fact]
    public async Task NewSessions_CanUseDifferentServersModelsAndKeys()
    {
        await using var client = Build();
        var first = await client.LLM.Responses.CreateAsync(Request(), Call("one"), TestContext.Current.CancellationToken);
        var second = await client.LLM.Responses.CreateAsync(Request(), Call("two"), TestContext.Current.CancellationToken);
        Assert.Equal("a", first.Metadata.TargetId);
        Assert.Equal("b", second.Metadata.TargetId);
        var calls = _provider.Calls.ToArray();
        Assert.Equal("https://a.invalid/api/v3/", calls[0].Context.Target.BaseAddress.AbsoluteUri);
        Assert.Equal("model-b", calls[1].Request["model"]!.GetValue<string>());
        Assert.Equal("key-b", calls[1].Context.Credential.Id);
        Assert.All(calls, call => Assert.Equal("public", call.Context.Metadata.PublicModel));
    }

    /// <summary>新客户端通过共享缓存恢复第二个目标，不重新调度。</summary>
    [Fact]
    public async Task Session_RetainsWholeTargetAcrossClientInstances()
    {
        await using var first = Build(selector: new TestTargetSelector(1));
        var original = await first.LLM.Responses.CreateAsync(Request(), Call("stable"), TestContext.Current.CancellationToken);
        await using var reconnected = Build(selector: new TestTargetSelector(0));
        var next = await reconnected.LLM.Responses.CreateAsync(Request(original.Id), Call("stable"), TestContext.Current.CancellationToken);
        Assert.Equal("b", next.Metadata.TargetId);
        Assert.Equal(original.Metadata, next.Metadata);
    }

    /// <summary>多个不同首次选择在共享协调后只能得到一个最终绑定。</summary>
    [Fact]
    public async Task ConcurrentFirstCalls_UseOneFinalBinding()
    {
        var tasks = Enumerable.Range(0, 20).Select(index => Task.Run(async () =>
        {
            await using var client = Build(selector: new TestTargetSelector(index));
            return await client.LLM.Responses.CreateAsync(Request(), Call("concurrent"), TestContext.Current.CancellationToken);
        }));
        var results = await Task.WhenAll(tasks);
        Assert.Single(results.Select(result => result.Metadata.TargetId).Distinct());
        Assert.Single(_provider.Calls.Select(call => call.Context.Credential.Id).Distinct());
    }

    /// <summary>配置或凭据变化不能把旧 Session 发到新目标。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingSession_RejectsRemovedOrChangedTarget(bool remove)
    {
        await using var first = Build();
        await first.LLM.Responses.CreateAsync(Request(), Call("bound"), TestContext.Current.CancellationToken);
        var targets = Targets();
        var replacement = remove ? new[] { targets[1] } : new[] { targets[0] with { UpstreamModel = "changed" }, targets[1] };
        await using var second = Build(replacement);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.Responses.CreateAsync(Request(), Call("bound"), TestContext.Current.CancellationToken));
        Assert.Single(_provider.Calls);
    }

    /// <summary>旧响应链缺少 Session 时不创建新绑定，也不调用上游。</summary>
    [Fact]
    public async Task PreviousResponse_RejectsMissingSession()
    {
        await using var client = Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LLM.Responses.CreateAsync(Request("old"), Call("missing"), TestContext.Current.CancellationToken));
        Assert.Null(await Store().GetAsync("missing", TestContext.Current.CancellationToken));
        Assert.Empty(_provider.Calls);
    }

    /// <summary>即使两个会话使用相同目标，也不能借用另一会话的响应 ID。</summary>
    [Fact]
    public async Task PreviousResponse_RejectsForeignSession()
    {
        await using var client = Build(selector: new TestTargetSelector(0));
        var foreign = await client.LLM.Responses.CreateAsync(Request(), Call("owner"), TestContext.Current.CancellationToken);
        await client.LLM.Responses.CreateAsync(Request(), Call("other"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LLM.Responses.CreateAsync(Request(foreign.Id), Call("other"), TestContext.Current.CancellationToken));
        Assert.Equal(2, _provider.Calls.Count);
    }

    /// <summary>流式响应登记归属，同时保留未知事件。</summary>
    [Fact]
    public async Task Streaming_PreservesUnknownEventsAndResponseOwnership()
    {
        await using var client = Build();
        List<ResponseEvent> events = [];
        await foreach (var item in client.LLM.Responses.StreamAsync(Request(), Call("stream"), TestContext.Current.CancellationToken)) events.Add(item);
        Assert.Equal("vendor.future", events[1].Type);
        Assert.Equal("不丢失", events[1].Data.GetProperty("value").GetString());
        var id = events[0].Data.GetProperty("response").GetProperty("id").GetString();
        await client.LLM.Responses.CreateAsync(Request(id), Call("stream"), TestContext.Current.CancellationToken);
        Assert.Equal(2, _provider.Calls.Count);
    }

    /// <summary>请求只替换实际模型与流开关，保留工具、推理与缓存扩展字段。</summary>
    [Fact]
    public async Task Request_PreservesUnknownProtocolFields()
    {
        await using var client = Build();
        var request = Request() with
        {
            Parameters = new()
            {
                ["store"] = JsonSerializer.SerializeToElement(false),
                ["caching"] = JsonSerializer.SerializeToElement(new { type = "enabled" }),
                ["opaque"] = JsonSerializer.SerializeToElement(new { encrypted_content = "opaque-token", future = new[] { 1, 2 } })
            }
        };
        var result = await client.LLM.Responses.CreateAsync(request, Call("fields"), TestContext.Current.CancellationToken);
        var sent = Assert.Single(_provider.Calls).Request;
        Assert.False(sent["store"]!.GetValue<bool>());
        Assert.Equal("opaque-token", sent["opaque"]!["encrypted_content"]!.GetValue<string>());
        Assert.Equal("public", request.Model);
        Assert.Equal("保留", result.Data.GetProperty("extra").GetString());
    }

    /// <summary>注册时复制路由集合，外部修改不会清空已经注册的配置。</summary>
    [Fact]
    public async Task Registration_SnapshotsMutableCollections()
    {
        var targets = Targets().ToList();
        var builder = new LokiClientBuilder();
        builder.AddLLM(OptionsFor(targets, new TestTargetSelector(0)));
        targets.Clear();
        await using var client = builder.Build();
        Assert.Equal("a", (await client.LLM.Responses.CreateAsync(Request(), Call("snapshot"), TestContext.Current.CancellationToken)).Metadata.TargetId);
    }

    /// <summary>一个已绑定 Session 不允许静默改变调用协议。</summary>
    [Fact]
    public async Task Session_RejectsProtocolChange()
    {
        await using var client = Build();
        await client.LLM.Responses.CreateAsync(Request(), Call("protocol"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LLM.ChatCompletions.CompleteAsync(
            new ChatCompletionRequest { Model = "public", Messages = [] }, Call("protocol"), TestContext.Current.CancellationToken));
        Assert.Single(_provider.Calls);
    }

    /// <summary>会话缓存对象不得包含实际密钥。</summary>
    [Fact]
    public async Task Binding_DoesNotContainApiKey()
    {
        await using var client = Build();
        await client.LLM.Responses.CreateAsync(Request(), Call("secret"), TestContext.Current.CancellationToken);
        var binding = await Store().GetAsync("secret", TestContext.Current.CancellationToken);
        Assert.NotNull(binding);
        Assert.DoesNotContain("secret-key-a", JsonSerializer.Serialize(binding));
    }

    /// <summary>取消的调用在上游执行之前终止。</summary>
    [Fact]
    public async Task Cancellation_DoesNotInvokeProvider()
    {
        await using var client = Build();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.LLM.Responses.CreateAsync(Request(), Call("cancel"), cancellation.Token));
        Assert.Empty(_provider.Calls);
    }

    /// <summary>缓存故障不能触发无状态路由或请求上游。</summary>
    [Fact]
    public async Task CacheFailure_DoesNotInvokeProvider()
    {
        var options = OptionsFor(Targets(), new TestTargetSelector(0)) with
        { SessionStore = new DistributedCacheLlmSessionStore(new FailingDistributedCache(), _sessionLock) };
        await using var client = new LokiClientBuilder().AddLLM(options).Build();
        await Assert.ThrowsAsync<IOException>(() => client.LLM.Responses.CreateAsync(Request(), Call("cache-failure"), TestContext.Current.CancellationToken));
        Assert.Empty(_provider.Calls);
    }

    /// <summary>协调租约丢失时不创建绑定，不请求上游。</summary>
    [Fact]
    public async Task LostLease_DoesNotCreateBindingOrInvokeProvider()
    {
        using var lost = new CancellationTokenSource();
        lost.Cancel();
        var options = OptionsFor(Targets(), new TestTargetSelector(0)) with
        { SessionStore = new DistributedCacheLlmSessionStore(_cache, new TestSessionLock(lost.Token)) };
        await using var client = new LokiClientBuilder().AddLLM(options).Build();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.LLM.Responses.CreateAsync(Request(), Call("lost"), TestContext.Current.CancellationToken));
        Assert.Null(await Store().GetAsync("lost", TestContext.Current.CancellationToken));
        Assert.Empty(_provider.Calls);
    }

    /// <summary>构建共享同一缓存但独立持有配置的客户端。</summary>
    private ILokiClient Build(IReadOnlyList<LlmTarget>? targets = null, ILlmTargetSelector? selector = null)
        => new LokiClientBuilder().AddLLM(OptionsFor(targets ?? Targets(), selector ?? new RoundRobinLlmTargetSelector())).Build();
    /// <summary>创建测试所需的缓存适配。</summary>
    private DistributedCacheLlmSessionStore Store() => new(_cache, _sessionLock);
    /// <summary>生成完整配置。</summary>
    private LlmOptions OptionsFor(IReadOnlyList<LlmTarget> targets, ILlmTargetSelector selector) => new()
    {
        SessionStore = Store(), Providers = [_provider],
        Credentials = [new("key-a", "secret-key-a"), new("key-b", "secret-key-b")],
        Routes = [new LlmRoute { Model = "public", Targets = targets }], TargetSelector = selector
    };
    /// <summary>配置不同地址、模型和凭据的两个目标。</summary>
    private static LlmTarget[] Targets() =>
    [
        new() { Id = "a", ProviderId = "stub", BaseAddress = new("https://a.invalid/api/v3/"), UpstreamModel = "model-a", CredentialId = "key-a" },
        new() { Id = "b", ProviderId = "stub", BaseAddress = new("https://b.invalid/api/v3/"), UpstreamModel = "model-b", CredentialId = "key-b" }
    ];
    /// <summary>创建保留公共模型名称的请求。</summary>
    private static ResponseRequest Request(string? previous = null) => new() { Model = "public", Input = JsonValue.Create("你好"), PreviousResponseId = previous };
    /// <summary>创建明确的逻辑会话。</summary>
    private static LlmCallOptions Call(string session) => new() { SessionId = session };
}
