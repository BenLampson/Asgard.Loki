using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asgard.Loki.LLM.Tests;

/// <summary>星火原生消息窗口和嵌套 domain 映射保持完整 SDK 会话目标，不生成其他协议。</summary>
public sealed class SparkCoreTests
{
    /// <summary>完整/流式公共入口的请求和每条消息与独立样本一致，不只返回首个文本项。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeWireAndWholeOrderedWindowMatchIndependentFixture(bool stream)
    {
        using var provider = new SparkStubProvider(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build(); var request = Request(); var before = JsonSerializer.Serialize(request); List<SparkEvent> events = [];
        if (stream) await foreach (var item in client.LLM.Spark.StreamAsync(request, new() { SessionId = "native" }, TestContext.Current.CancellationToken)) events.Add(item);
        else { var result = await client.LLM.Spark.CompleteAsync(request, new() { SessionId = "native" }, TestContext.Current.CancellationToken); events.AddRange(result.Events); Assert.Equal(new LlmResultMetadata("public-spark", "lite", provider.Id, "a"), result.Metadata); }
        Assert.True(JsonElement.DeepEquals(SparkStubProvider.Part("core_wire"), JsonSerializer.SerializeToElement(Assert.Single(provider.Calls).Body))); Assert.Equal(before, JsonSerializer.Serialize(request));
        var golden = SparkStubProvider.Part("frames").EnumerateArray().ToArray(); Assert.Equal(golden.Length, events.Count);
        for (var i = 0; i < golden.Length; i++) { Assert.True(JsonElement.DeepEquals(golden[i], events[i].Data)); Assert.Equal(new LlmResultMetadata("public-spark", "lite", provider.Id, "a"), events[i].Metadata); Assert.Null(events[i].Id); }
        Assert.Equal(2, events[1].Data.GetProperty("payload").GetProperty("choices").GetProperty("text").GetArrayLength()); var body = Assert.Single(provider.Calls).Body; Assert.False(body.ContainsKey("model")); Assert.False(body.ContainsKey("stream")); Assert.False(body["header"]!.AsObject().ContainsKey("app_id")); Assert.Equal("9007199254740993", body["parameter"]!["chat"]!["future"]!["exact"]!.ToJsonString()); Assert.Equal("9007199254740993", events[^1].Data.GetProperty("payload").GetProperty("usage").GetProperty("text").GetProperty("future").GetRawText());
    }
    /// <summary>同一 Session 保持整套服务器/模型/凭据，新 Session 才选择另一个完整目标。</summary>
    [Fact]
    public async Task SessionKeepsWholeTargetInsteadOfRotatingIndividualKeys()
    {
        using var provider = new SparkStubProvider(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build();
        foreach (var id in new[] { "same", "same", "other" }) await client.LLM.Spark.CompleteAsync(Request(), new() { SessionId = id }, TestContext.Current.CancellationToken);
        var calls = provider.Calls.ToArray(); Assert.Equal(new[] { "a", "a", "b" }, calls.Select(c => c.Context.Target.Id)); Assert.Equal("https://a.example.invalid/v1.1/chat", calls[0].Context.Target.BaseAddress.AbsoluteUri); Assert.Equal("fixture-key-a", calls[0].Context.Credential.ApiKey);
        Assert.Equal("https://b.example.invalid/v3.1/chat", calls[2].Context.Target.BaseAddress.AbsoluteUri); Assert.Equal("fixture-key-b", calls[2].Context.Credential.ApiKey); Assert.Equal("generalv3", calls[2].Body["parameter"]!["chat"]!["domain"]!.GetValue<string>());
    }
    /// <summary>任何完整绑定成员漂移都拒绝，不在已经建立的 Session 下换目标或模型。</summary>
    [Theory] [InlineData("server")] [InlineData("model")] [InlineData("credential")] [InlineData("adapter")] [InlineData("version")] [InlineData("protocol")] [InlineData("removed")]
    public async Task BindingDriftFailsBeforeAnotherAdapterCall(string kind)
    {
        using var provider = new SparkStubProvider(); var config = Configuration(provider); await using (var first = new LokiClientBuilder().AddLLM(config).Build()) await first.LLM.Spark.CompleteAsync(Request(), new() { SessionId = "drift" }, TestContext.Current.CancellationToken);
        var targets = config.Routes[0].Targets.ToArray(); targets[0] = kind switch
        {
            "server" => targets[0] with { BaseAddress = new("https://changed.example.invalid/v1.1/chat") }, "model" => targets[0] with { UpstreamModel = "different" },
            "version" => targets[0] with { Version = "2" }, "protocol" => targets[0] with { Protocols = LlmProtocol.Spark | LlmProtocol.ChatCompletions }, _ => targets[0]
        };
        if (kind == "removed") targets = [targets[1]]; using var changed = new SparkStubProvider("changed-implementation"); config = config with { Routes = [new() { Model = "public-spark", Targets = targets }] };
        if (kind == "credential") config = config with { Credentials = [new("key-a", "changed-key"), config.Credentials[1]] }; if (kind == "adapter") config = config with { Providers = [changed] };
        await using var second = new LokiClientBuilder().AddLLM(config).Build(); await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.Spark.CompleteAsync(Request(), new() { SessionId = "drift" }, TestContext.Current.CancellationToken)); Assert.Single(provider.Calls); Assert.Empty(changed.Calls);
    }
    /// <summary>同一目标即使声明两种协议，既有 Chat Session 也不能成为 Spark 会话。</summary>
    [Fact]
    public async Task ExistingChatSessionCannotSwitchToSpark()
    {
        using var provider = new SparkStubProvider(); var config = Configuration(provider); config = config with { Routes = [new() { Model = "public-spark", Targets = config.Routes[0].Targets.Select(t => t with { Protocols = LlmProtocol.ChatCompletions | LlmProtocol.Spark }).ToArray() }] }; await using var client = new LokiClientBuilder().AddLLM(config).Build(); var call = new LlmCallOptions { SessionId = "bound" };
        await client.LLM.ChatCompletions.CompleteAsync(new() { Model = "public-spark", Messages = [] }, call, TestContext.Current.CancellationToken); await Assert.ThrowsAsync<InvalidOperationException>(() => client.LLM.Spark.CompleteAsync(Request(), call, TestContext.Current.CancellationToken)); Assert.Empty(provider.Calls); Assert.Equal(1, provider.ChatCalls);
    }
    /// <summary>原生 sid 不登记为 Responses 延续 ID，新增协议数值保留已有位的语义。</summary>
    [Fact]
    public async Task NativeSidIsNotResponsesOwnership()
    {
        using var provider = new SparkStubProvider(); var config = Configuration(provider); await using var client = new LokiClientBuilder().AddLLM(config).Build(); await client.LLM.Spark.CompleteAsync(Request(), new() { SessionId = "ownership" }, TestContext.Current.CancellationToken); var binding = await config.SessionStore!.GetAsync("ownership", TestContext.Current.CancellationToken);
        Assert.NotNull(binding); Assert.Equal(LlmProtocol.Spark, binding.Protocol); Assert.False(await config.SessionStore.OwnsResponseAsync(binding, "spark-native-sid", TestContext.Current.CancellationToken)); Assert.Equal(2048, (int)LlmProtocol.Hunyuan); Assert.Equal(4096, (int)LlmProtocol.Spark);
    }
    internal static SparkRequest Request() => JsonSerializer.Deserialize<SparkRequest>(SparkStubProvider.Part("caller"))!;
    internal static LlmOptions Configuration(ILlmProvider provider) => new()
    {
        Providers = [provider], Credentials = [new("key-a", "fixture-key-a"), new("key-b", "fixture-key-b")],
        SessionStore = new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock()),
        Routes = [new() { Model = "public-spark", Targets = [Target("a", provider.Id), Target("b", provider.Id)] }]
    };
    private static LlmTarget Target(string id, string provider) => new()
    { Id = id, ProviderId = provider, BaseAddress = new("https://" + id + ".example.invalid/" + (id == "a" ? "v1.1" : "v3.1") + "/chat"), UpstreamModel = id == "a" ? "lite" : "generalv3", CredentialId = "key-" + id, Protocols = LlmProtocol.Spark };
}
