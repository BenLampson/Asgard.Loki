using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.LLM.Tests;

/// <summary>混元原生入口保持完整目标、Session 及无损原生结构，全部离线。</summary>
public sealed class HunyuanCoreTests
{
    /// <summary>对独立黄金正文逐字段比较，原生工具关联、字符串 schema 与结果信封保持。</summary>
    [Fact]
    public async Task NativePayloadAndWholeEnvelopeMatchIndependentFixture()
    {
        using var provider = new HunyuanStubProvider(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build();
        var request = Request(); var before = JsonSerializer.Serialize(request);
        var result = await client.LLM.Hunyuan.CompleteAsync(request, new() { SessionId = "native" }, TestContext.Current.CancellationToken);
        var call = Assert.Single(provider.Calls); Assert.True(JsonElement.DeepEquals(HunyuanStubProvider.Fixture("wire_complete"), JsonSerializer.SerializeToElement(call.Body)));
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.True(JsonElement.DeepEquals(HunyuanStubProvider.Fixture("response"), result.Data));
        Assert.Equal(new LlmResultMetadata("public", "native-fixed", provider.Id, "a"), result.Metadata);
        Assert.Null(result.Id); var native = result.Data.GetProperty("Response"); Assert.Equal("hunyuan-native", native.GetProperty("Id").GetString());
        Assert.Equal(new[] { 1, 0 }, native.GetProperty("Choices").EnumerateArray().Select(c => c.GetProperty("Index").GetInt32()));
        Assert.Equal("call-history", call.Body["Messages"]![3]!["ToolCallId"]!.GetValue<string>()); Assert.Equal("9007199254740993", call.Body["Future"]!["Exact"]!.ToJsonString());
        Assert.Equal(JsonValueKind.String, JsonSerializer.SerializeToElement(call.Body["Tools"]![0]!["Function"]!["Parameters"]).ValueKind);
        Assert.False(call.Body.ContainsKey("model")); Assert.False(call.Body.ContainsKey("stream"));
    }
    /// <summary>同一会话不变更服务器、实际模型或 Key，新会话才调度其他完整目标。</summary>
    [Fact]
    public async Task SessionKeepsWholeTargetAndNewSessionsRotate()
    {
        using var provider = new HunyuanStubProvider(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build();
        foreach (var session in new[] { "bound", "bound", "other" }) await client.LLM.Hunyuan.CompleteAsync(Request(), new() { SessionId = session }, TestContext.Current.CancellationToken);
        var calls = provider.Calls.ToArray(); Assert.Equal(new[] { "a", "a", "b" }, calls.Select(c => c.Context.Target.Id));
        Assert.Equal("https://a.example.invalid/", calls[0].Context.Target.BaseAddress.AbsoluteUri); Assert.Equal("fixture-key-a", calls[0].Context.Credential.ApiKey);
        Assert.Equal("https://b.example.invalid/", calls[2].Context.Target.BaseAddress.AbsoluteUri); Assert.Equal("fixture-key-b", calls[2].Context.Credential.ApiKey); Assert.Equal("native-second", calls[2].Body["Model"]!.GetValue<string>());
    }
    /// <summary>完整绑定任一成员漂移都拒绝，不借用其他目标继续调用。</summary>
    [Theory] [InlineData("server")] [InlineData("model")] [InlineData("credential")] [InlineData("adapter")] [InlineData("version")] [InlineData("protocol")] [InlineData("removed")]
    public async Task BindingDriftCannotSwitchToAnotherTarget(string change)
    {
        using var provider = new HunyuanStubProvider(); var configuration = Configuration(provider);
        await using (var first = new LokiClientBuilder().AddLLM(configuration).Build()) await first.LLM.Hunyuan.CompleteAsync(Request(), new() { SessionId = "drift" }, TestContext.Current.CancellationToken);
        var targets = configuration.Routes[0].Targets.ToArray();
        targets[0] = change switch
        {
            "server" => targets[0] with { BaseAddress = new("https://changed.example.invalid/") },
            "model" => targets[0] with { UpstreamModel = "changed" }, "version" => targets[0] with { Version = "2" },
            "protocol" => targets[0] with { Protocols = LlmProtocol.Hunyuan | LlmProtocol.ChatCompletions }, _ => targets[0]
        };
        if (change == "removed") targets = [targets[1]];
        using var changedProvider = new HunyuanStubProvider("changed-implementation");
        configuration = configuration with { Routes = [new() { Model = "public", Targets = targets }] };
        if (change == "credential") configuration = configuration with { Credentials = [new("key-a", "changed-fixture"), configuration.Credentials[1]] };
        if (change == "adapter") configuration = configuration with { Providers = [changedProvider] };
        await using var second = new LokiClientBuilder().AddLLM(configuration).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.Hunyuan.CompleteAsync(Request(), new() { SessionId = "drift" }, TestContext.Current.CancellationToken));
        Assert.Single(provider.Calls); Assert.Empty(changedProvider.Calls);
    }
    /// <summary>即使目标支持多协议，已有 Chat Session 也不能切换为混元原生调用。</summary>
    [Fact]
    public async Task ExistingChatSessionCannotBecomeHunyuanSession()
    {
        using var provider = new HunyuanStubProvider(); var configuration = Configuration(provider);
        configuration = configuration with { Routes = [new() { Model = "public", Targets = configuration.Routes[0].Targets.Select(t => t with { Protocols = LlmProtocol.ChatCompletions | LlmProtocol.Hunyuan }).ToArray() }] };
        await using var client = new LokiClientBuilder().AddLLM(configuration).Build(); var options = new LlmCallOptions { SessionId = "chat-bound" };
        await client.LLM.ChatCompletions.CompleteAsync(new() { Model = "public", Messages = [] }, options, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LLM.Hunyuan.CompleteAsync(Request(), options, TestContext.Current.CancellationToken)); Assert.Empty(provider.Calls); Assert.Equal(1, provider.ChatCalls);
    }
    /// <summary>上游错误原样传播，仅一次尝试；能力模块不释放借用适配器。</summary>
    [Fact]
    public async Task FailureDoesNotRetryOrDisposeBorrowedProvider()
    {
        var failure = new IOException("fixture failure"); using var provider = new HunyuanStubProvider { Failure = failure };
        await using (var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build())
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => client.LLM.Hunyuan.CompleteAsync(Request(), new() { SessionId = "failed" }, TestContext.Current.CancellationToken)));
        Assert.Single(provider.Calls); Assert.False(provider.Disposed);
    }
    /// <summary>结果中的混元 ID 不创建 Responses 延续归属。</summary>
    [Fact]
    public async Task NativeIdsDoNotCreateResponsesOwnership()
    {
        using var provider = new HunyuanStubProvider(); var configuration = Configuration(provider); await using var client = new LokiClientBuilder().AddLLM(configuration).Build();
        await client.LLM.Hunyuan.CompleteAsync(Request(), new() { SessionId = "ownership" }, TestContext.Current.CancellationToken);
        var binding = await configuration.SessionStore!.GetAsync("ownership", TestContext.Current.CancellationToken); Assert.NotNull(binding); Assert.Equal(LlmProtocol.Hunyuan, binding.Protocol);
        Assert.False(await configuration.SessionStore.OwnsResponseAsync(binding, "hunyuan-native", TestContext.Current.CancellationToken)); Assert.Equal(2048, (int)LlmProtocol.Hunyuan);
    }
    /// <summary>核心不补造 Messages、工具或默认生成参数；原生输入要求归适配器。</summary>
    [Theory] [InlineData("absent")] [InlineData("null")] [InlineData("[]")]
    public async Task NativeMessagesShapeStaysCallerControlled(string json)
    {
        using var provider = new HunyuanStubProvider(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build(); var request = new HunyuanRequest { Model = "public" };
        if (json != "absent") request.Parameters.Add("Messages", JsonSerializer.Deserialize<JsonElement>(json));
        await client.LLM.Hunyuan.CompleteAsync(request, new() { SessionId = "shape" }, TestContext.Current.CancellationToken); var body = Assert.Single(provider.Calls).Body;
        Assert.Equal(json != "absent", body.ContainsKey("Messages")); Assert.Equal(json == "absent" ? 2 : 3, body.Count);
        if (json != "absent") Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(json), JsonSerializer.SerializeToElement(body["Messages"])));
    }
    internal static HunyuanRequest Request() => JsonSerializer.Deserialize<HunyuanRequest>(HunyuanStubProvider.Fixture("caller"))!;
    internal static LlmOptions Configuration(ILlmProvider provider) => new()
    {
        Providers = [provider], Credentials = [new("key-a", "fixture-key-a"), new("key-b", "fixture-key-b")],
        SessionStore = new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock()),
        Routes = [new() { Model = "public", Targets = [Target("a", provider.Id), Target("b", provider.Id)] }]
    };
    private static LlmTarget Target(string id, string provider) => new()
    { Id = id, ProviderId = provider, BaseAddress = new("https://" + id + ".example.invalid/"), UpstreamModel = id == "a" ? "native-fixed" : "native-second", CredentialId = "key-" + id, Protocols = LlmProtocol.Hunyuan };
}
