using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Tests;

/// <summary>原生文本协议保持完整 Session 与原始字节语义，不填充正文模型或 JSON 终态。</summary>
public sealed class WorkersAiCoreTests
{
    /// <summary>原生完整请求和 SSE 窗口与独立样本一致，选定实际模型只存在于目标上下文。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FullNativeWindows_ArePreserved(bool stream)
    {
        using var provider = new WorkersAiStubProvider(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build(); var request = Request(); var before = JsonSerializer.Serialize(request);
        if (stream)
        {
            var expected = WorkersAiStubProvider.Part("events").EnumerateArray().ToArray(); List<WorkersAiTextEvent> events = [];
            await foreach (var item in client.LLM.WorkersAiText.StreamAsync(request, new() { SessionId = "native" }, TestContext.Current.CancellationToken)) events.Add(item);
            Assert.Equal(expected.Length, events.Count);
            for (var i = 0; i < events.Count; i++)
            {
                Assert.Equal(expected[i].GetProperty("data").GetString(), events[i].Data); Assert.Equal(expected[i].GetProperty("event_name").GetString(), events[i].EventName);
                Assert.Equal(expected[i].GetProperty("event_id").GetString(), events[i].EventId); Assert.Equal(expected[i].GetProperty("retry").GetInt64(), events[i].RetryMilliseconds);
                Assert.Equal(i == events.Count - 1, events[i].IsTerminal); Assert.Equal(i != events.Count - 1, events[i].Json.HasValue);
                if (events[i].Json is { } json) Assert.Equal(events[i].Data, json.GetRawText());
            }
            Assert.Equal("[DONE]", events[^1].Data); Assert.Null(events[^1].Json); Assert.False(events[0].IsTerminal);
        }
        else
        {
            var result = await client.LLM.WorkersAiText.CompleteAsync(request, new() { SessionId = "native" }, TestContext.Current.CancellationToken);
            Assert.Equal(WorkersAiStubProvider.Part("response").GetRawText(), result.Data.GetRawText()); Assert.Null(result.Id);
            Assert.Equal(new LlmResultMetadata("public-workers-text", "@cf/fixture/text", provider.Id, "a"), result.Metadata);
        }
        var call = Assert.Single(provider.Calls); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(WorkersAiStubProvider.Part(stream ? "wire_stream" : "wire_complete").GetRawText()), call.Body));
        Assert.False(call.Body.ContainsKey("model")); Assert.Equal("@cf/fixture/text", call.Context.Target.UpstreamModel); Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Equal("9007199254740993", call.Body["future"]!["exact"]!.ToJsonString());
    }
    /// <summary>同一 Session 保留服务器/账号、模型和凭据，新 Session 才选择下一个完整目标。</summary>
    [Fact]
    public async Task Session_KeepsWholeUpstream()
    {
        using var provider = new WorkersAiStubProvider(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build();
        foreach (var id in new[] { "same", "same", "other" }) await client.LLM.WorkersAiText.CompleteAsync(Request(), new() { SessionId = id }, TestContext.Current.CancellationToken);
        var calls = provider.Calls.ToArray(); Assert.Equal(new[] { "a", "a", "b" }, calls.Select(c => c.Context.Target.Id));
        Assert.Equal("fixture-a", calls[0].Context.Credential.ApiKey); Assert.Equal("fixture-b", calls[2].Context.Credential.ApiKey); Assert.Equal("@cf/fixture/other-text", calls[2].Context.Target.UpstreamModel);
    }
    /// <summary>完整目标的任一成员变化都失败，不以同模型或同 Key 为由迁移。</summary>
    [Theory] [InlineData("server")] [InlineData("model")] [InlineData("credential")] [InlineData("adapter")] [InlineData("version")] [InlineData("protocol")] [InlineData("removed")]
    public async Task BindingDrift_DoesNotReselect(string kind)
    {
        using var provider = new WorkersAiStubProvider(); var options = Configuration(provider);
        await using (var first = new LokiClientBuilder().AddLLM(options).Build()) await first.LLM.WorkersAiText.CompleteAsync(Request(), new() { SessionId = "bound" }, TestContext.Current.CancellationToken);
        var targets = options.Routes[0].Targets.ToArray(); targets[0] = kind switch
        {
            "server" => targets[0] with { BaseAddress = new("https://other.invalid/accounts/other/ai/run/") }, "model" => targets[0] with { UpstreamModel = "@cf/fixture/changed" },
            "version" => targets[0] with { Version = "changed" }, "protocol" => targets[0] with { Protocols = LlmProtocol.WorkersAiText | LlmProtocol.ChatCompletions }, _ => targets[0]
        };
        if (kind == "removed") targets = [targets[1]]; using var replacement = new WorkersAiStubProvider("changed");
        options = options with { Routes = [new() { Model = "public-workers-text", Targets = targets }] };
        if (kind == "credential") options = options with { Credentials = [new("a", "changed-fixture"), options.Credentials[1]] }; if (kind == "adapter") options = options with { Providers = [replacement] };
        await using var next = new LokiClientBuilder().AddLLM(options).Build(); await Assert.ThrowsAsync<InvalidOperationException>(() => next.LLM.WorkersAiText.CompleteAsync(Request(), new() { SessionId = "bound" }, TestContext.Current.CancellationToken));
        Assert.Single(provider.Calls); Assert.Empty(replacement.Calls);
    }
    /// <summary>原生请求既可携带消息，也可明确携带 prompt；核心不选择模板或填充缺省字段。</summary>
    [Fact]
    public async Task PromptOnlyBody_RemainsNative()
    {
        using var provider = new WorkersAiStubProvider(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build(); var native = WorkersAiStubProvider.Part("prompt_case");
        await client.LLM.WorkersAiText.CompleteAsync(JsonSerializer.Deserialize<WorkersAiTextRequest>(native.GetProperty("caller"))!, new() { SessionId = "prompt" }, TestContext.Current.CancellationToken);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(native.GetProperty("wire").GetRawText()), Assert.Single(provider.Calls).Body));
    }
    /// <summary>新增位保持旧协议含义，原生响应的业务 ID 不建立 Responses 延续。</summary>
    [Fact]
    public async Task NativeProtocolAndOwnership_RemainSeparate()
    {
        Assert.Equal(16384, (int)LlmProtocol.WorkersAiText); Assert.Equal(8192, (int)LlmProtocol.CohereChat); Assert.Equal(4096, (int)LlmProtocol.Spark);
        using var provider = new WorkersAiStubProvider(); var options = Configuration(provider); await using var client = new LokiClientBuilder().AddLLM(options).Build();
        await client.LLM.WorkersAiText.CompleteAsync(Request(), new() { SessionId = "ownership" }, TestContext.Current.CancellationToken);
        var binding = await options.SessionStore!.GetAsync("ownership", TestContext.Current.CancellationToken); Assert.NotNull(binding); Assert.Equal(LlmProtocol.WorkersAiText, binding.Protocol);
        Assert.False(await options.SessionStore.OwnsResponseAsync(binding, "business-only", TestContext.Current.CancellationToken));
    }
    internal static WorkersAiTextRequest Request() => JsonSerializer.Deserialize<WorkersAiTextRequest>(WorkersAiStubProvider.Part("caller"))!;
    internal static LlmOptions Configuration(ILlmProvider provider) => new()
    {
        Providers = [provider], Credentials = [new("a", "fixture-a"), new("b", "fixture-b")],
        SessionStore = new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock()),
        Routes = [new() { Model = "public-workers-text", Targets = [Target("a", provider.Id), Target("b", provider.Id)] }]
    };
    private static LlmTarget Target(string id, string provider) => new()
    { Id = id, ProviderId = provider, BaseAddress = new("https://native.invalid/client/v4/accounts/" + id + "/ai/run/"), UpstreamModel = id == "a" ? "@cf/fixture/text" : "@cf/fixture/other-text", CredentialId = id, Protocols = LlmProtocol.WorkersAiText };
}
