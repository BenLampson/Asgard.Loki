using Asgard.Loki.LLM.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>HTTP 策略属于完整供应商绑定，同配置跨实例恢复，不同配置在发包前失败。</summary>
public sealed class HttpSessionTests
{
    /// <summary>HTTP 字节、尾部策略和命名客户端变化都必须显式开始新 Session。</summary>
    [Theory]
    [InlineData("bytes")] [InlineData("tail")] [InlineData("client")]
    public async Task ChangedPolicy_RejectsExistingSessionBeforeHttp(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(CompactionFixture.Json("{\"id\":\"r1\",\"status\":\"completed\",\"output\":[]}")));
        var store = CompactionFixture.Store(); var factory = new OpenAiHttpFactory(handler);
        await using (var first = CompactionFixture.Build(handler, store, provider: new OpenAiLlmProvider(factory)))
            await first.LLM.Responses.CreateAsync(new() { Model = "public" }, CompactionFixture.Call(), TestContext.Current.CancellationToken);
        var policy = new OpenAiHttpOptions { MaximumBodyBytes = change == "bytes" ? 1024 : 32 * 1024 * 1024, MaximumBufferedTerminalEvents = change == "tail" ? 2 : 128 };
        await using var second = CompactionFixture.Build(handler, store, provider: new OpenAiLlmProvider(factory, policy, httpClientName: change == "client" ? "other" : "Asgard.Loki.LLM.OpenAI"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.Responses.CreateAsync(new() { Model = "public" }, CompactionFixture.Call(), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls); Assert.Single(factory.Clients);
    }

    /// <summary>实例重建和调度器变化不能替换已绑定的服务器、模型、凭据或原生响应链。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RecreatedClient_RecoversCompleteTarget(bool streaming)
    {
        var store = CompactionFixture.Store(); var calls = 0;
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("b.invalid", request.RequestUri!.Host); Assert.Equal("test-key-b", request.Headers.Authorization!.Parameter);
            var text = await request.Content!.ReadAsStringAsync(token); Assert.Contains("actual-b", text);
            if (calls++ == 0) return CompactionFixture.Json("{\"id\":\"r1\",\"status\":\"completed\",\"output\":[]}");
            Assert.Contains("r1", text);
            return streaming ? OpenAiHttpFixture.Reply(new(OpenAiHttpFixture.Bytes(OpenAiHttpFixture.Frame("{\"type\":\"response.completed\",\"response\":{\"id\":\"r2\",\"status\":\"completed\",\"output\":[]}}"))), true)
                : CompactionFixture.Json("{\"id\":\"r2\",\"status\":\"completed\",\"output\":[]}");
        });
        var targets = new[] { CompactionFixture.Target("a"), CompactionFixture.Target("b") };
        await using (var first = CompactionFixture.Build(handler, store, targets, new TestTargetSelector(1)))
            await first.LLM.Responses.CreateAsync(new() { Model = "public" }, CompactionFixture.Call(), TestContext.Current.CancellationToken);
        await using var second = CompactionFixture.Build(handler, store, targets, new TestTargetSelector(0));
        var request = new ResponseRequest { Model = "public", PreviousResponseId = "r1" };
        if (streaming) { await foreach (var item in second.LLM.Responses.StreamAsync(request, CompactionFixture.Call(), TestContext.Current.CancellationToken)) Assert.Equal("b", item.Metadata.TargetId); }
        else Assert.Equal("b", (await second.LLM.Responses.CreateAsync(request, CompactionFixture.Call(), TestContext.Current.CancellationToken)).Metadata.TargetId);
        Assert.Equal(2, handler.Calls); Assert.Equal("b", (await store.GetAsync("session", TestContext.Current.CancellationToken))!.TargetId);
    }
}
