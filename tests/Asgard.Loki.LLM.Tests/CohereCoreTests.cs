using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.LLM.Tests;

/// <summary>原生 Cohere 入口的完整目标、不可变快照与原生工具关联离线契约。</summary>
public sealed class CohereCoreTests
{
    /// <summary>全量请求和事件与独立样本逐字段比较，只允许明确目标和流模式变化。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativePayloadAndResults_RemainComplete(bool stream)
    {
        using var provider = new CohereStubProvider(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build();
        var request = Request(); var before = JsonSerializer.Serialize(request);
        if (stream)
        {
            using var expected = JsonDocument.Parse(CohereStubProvider.Fixture("events.json")); List<CohereChatEvent> events = [];
            await foreach (var item in client.LLM.CohereChat.StreamAsync(request, new() { SessionId = "native" }, TestContext.Current.CancellationToken)) events.Add(item);
            Assert.Equal(expected.RootElement.GetArrayLength(), events.Count);
            for (var i = 0; i < events.Count; i++)
            {
                Assert.Equal(expected.RootElement[i].GetRawText(), events[i].Data.GetRawText());
                Assert.Null(events[i].EventName); Assert.Null(events[i].EventId); Assert.Null(events[i].RetryMilliseconds); Assert.Null(events[i].Id);
            }
            Assert.Equal("text-generation", events[3].Type); Assert.False(events[3].IsTerminal); Assert.True(events[5].IsTerminal); Assert.False(events[6].IsTerminal);
        }
        else
        {
            var result = await client.LLM.CohereChat.CompleteAsync(request, new() { SessionId = "native" }, TestContext.Current.CancellationToken);
            using var expected = JsonDocument.Parse(CohereStubProvider.Fixture("response.json"));
            Assert.Equal(expected.RootElement.GetRawText(), result.Data.GetRawText()); Assert.Null(result.Id);
            Assert.Equal("generation/原始", result.GenerationId); Assert.Equal("response/distinct", result.ResponseId); Assert.Equal("COMPLETE", result.FinishReason);
            Assert.Equal(new LlmResultMetadata("public-cohere", "command-configured-by-target", provider.Id, "a"), result.Metadata);
        }
        var call = Assert.Single(provider.Calls); using var wire = JsonDocument.Parse(CohereStubProvider.Fixture("wire.json"));
        var expectedBody = System.Text.Json.Nodes.JsonNode.Parse(wire.RootElement.GetRawText())!.AsObject(); expectedBody["stream"] = stream;
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(expectedBody, call.Body)); Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Equal("18446744073709551615", call.Body["seed"]!.ToJsonString());
        Assert.Equal("9007199254740993", call.Body["tool_results"]![0]!["call"]!["parameters"]!["precision"]!.ToJsonString());
        Assert.Equal("上海", call.Body["tool_results"]![0]!["call"]!["parameters"]!["city"]!.GetValue<string>());
        Assert.Equal("北京", call.Body["tool_results"]![1]!["call"]!["parameters"]!["city"]!.GetValue<string>());
        Assert.False(call.Body.ContainsKey("max_tokens")); Assert.False(call.Body.ContainsKey("safety_mode"));
    }
    /// <summary>目标选择只发生于新 Session，整组服务器、模型和凭据保持绑定。</summary>
    [Fact]
    public async Task Session_BindsWholeTarget()
    {
        using var provider = new CohereStubProvider(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build();
        foreach (var id in new[] { "bound", "bound", "new" }) await client.LLM.CohereChat.CompleteAsync(Request(), new() { SessionId = id }, TestContext.Current.CancellationToken);
        var calls = provider.Calls.ToArray(); Assert.Equal(new[] { "a", "a", "b" }, calls.Select(c => c.Context.Target.Id));
        Assert.Equal("https://a.example.invalid/tenant/v1/", calls[0].Context.Target.BaseAddress.AbsoluteUri); Assert.Equal("fixture-a", calls[0].Context.Credential.ApiKey);
        Assert.Equal("command-b", calls[2].Body["model"]!.GetValue<string>()); Assert.Equal("fixture-b", calls[2].Context.Credential.ApiKey);
    }
    /// <summary>完整绑定中的任何漂移都不能失效转移或重用旧适配实现。</summary>
    [Theory] [InlineData("server")] [InlineData("model")] [InlineData("credential")] [InlineData("adapter")] [InlineData("version")] [InlineData("protocol")] [InlineData("removed")]
    public async Task BindingDrift_FailsWithoutReselection(string change)
    {
        using var provider = new CohereStubProvider(); var options = Configuration(provider);
        await using (var first = new LokiClientBuilder().AddLLM(options).Build()) await first.LLM.CohereChat.CompleteAsync(Request(), new() { SessionId = "drift" }, TestContext.Current.CancellationToken);
        var targets = options.Routes[0].Targets.ToArray(); targets[0] = change switch
        {
            "server" => targets[0] with { BaseAddress = new("https://changed.invalid/v1/") },
            "model" => targets[0] with { UpstreamModel = "changed" }, "version" => targets[0] with { Version = "changed" },
            "protocol" => targets[0] with { Protocols = LlmProtocol.CohereChat | LlmProtocol.ChatCompletions }, _ => targets[0]
        };
        if (change == "removed") targets = [targets[1]];
        using var replacement = new CohereStubProvider("changed"); options = options with { Routes = [new() { Model = "public-cohere", Targets = targets }] };
        if (change == "credential") options = options with { Credentials = [new("a", "changed-fixture"), options.Credentials[1]] };
        if (change == "adapter") options = options with { Providers = [replacement] };
        await using var next = new LokiClientBuilder().AddLLM(options).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => next.LLM.CohereChat.CompleteAsync(Request(), new() { SessionId = "drift" }, TestContext.Current.CancellationToken));
        Assert.Single(provider.Calls); Assert.Empty(replacement.Calls);
    }
    /// <summary>新协议位不改变已发布协议值；可选 ID 和结束原因不被补造。</summary>
    [Fact]
    public void NativeViews_KeepOptionalFieldsAndStableProtocolBits()
    {
        Assert.Equal(8192, (int)LlmProtocol.CohereChat); Assert.Equal(4096, (int)LlmProtocol.Spark); Assert.Equal(2048, (int)LlmProtocol.Hunyuan);
        using var document = JsonDocument.Parse(CohereStubProvider.Fixture("minimal-response.json"));
        var result = new CohereChatCompletion(document.RootElement, new("public", "actual", "provider", "target"));
        Assert.Null(result.GenerationId); Assert.Null(result.ResponseId); Assert.Null(result.FinishReason); Assert.Null(result.Id);
        Assert.Equal("", result.Data.GetProperty("text").GetString()); Assert.Equal(2, result.Data.GetProperty("tool_calls").GetArrayLength());
    }
    /// <summary>原生响应身份不进入 Responses 所有权索引。</summary>
    [Fact]
    public async Task CohereIds_DoNotBecomeResponsesContinuation()
    {
        using var provider = new CohereStubProvider(); var options = Configuration(provider); await using var client = new LokiClientBuilder().AddLLM(options).Build();
        var result = await client.LLM.CohereChat.CompleteAsync(Request(), new() { SessionId = "ownership" }, TestContext.Current.CancellationToken);
        var binding = await options.SessionStore!.GetAsync("ownership", TestContext.Current.CancellationToken); Assert.NotNull(binding); Assert.Equal(LlmProtocol.CohereChat, binding.Protocol);
        foreach (var id in new[] { result.GenerationId!, result.ResponseId! }) Assert.False(await options.SessionStore.OwnsResponseAsync(binding, id, TestContext.Current.CancellationToken));
    }
    internal static CohereChatRequest Request() => JsonSerializer.Deserialize<CohereChatRequest>(CohereStubProvider.Fixture("request.json"))!;
    internal static LlmOptions Configuration(ILlmProvider provider) => new()
    {
        Providers = [provider], Credentials = [new("a", "fixture-a"), new("b", "fixture-b")],
        SessionStore = new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock()),
        Routes = [new() { Model = "public-cohere", Targets = [Target("a", provider.Id), Target("b", provider.Id)] }]
    };
    private static LlmTarget Target(string id, string provider) => new()
    { Id = id, ProviderId = provider, BaseAddress = new("https://" + id + ".example.invalid/tenant/v1/"), UpstreamModel = id == "a" ? "command-configured-by-target" : "command-b", CredentialId = id, Protocols = LlmProtocol.CohereChat };
}
