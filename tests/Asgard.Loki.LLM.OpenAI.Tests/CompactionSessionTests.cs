using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.CompactionFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>压缩和后续生成保持相同完整目标；压缩 ID 不伪装为可续接生成响应。</summary>
public sealed class CompactionSessionTests
{
    /// <summary>竞争时即使提出的目标支持压缩，也必须尊重不支持压缩的最终胜出目标并失败。</summary>
    [Fact]
    public async Task FirstBindingRace_DoesNotOverrideWinningTarget()
    {
        using var handler = new StubHttpHandler((request, _) =>
        {
            Assert.Equal("a.invalid", request.RequestUri!.Host);
            return Task.FromResult(Json("""{"id":"resp_a","status":"completed","output":[]}"""));
        });
        var store = Store(); var targets = new[] { Target("a", LlmTargetFeatures.None), Target("b") };
        await using var winner = Build(handler, store, targets, new TestTargetSelector(0));
        await winner.LLM.Responses.CreateAsync(new() { Model = "public" }, Call(), TestContext.Current.CancellationToken);
        var stale = new StaleReadSessionStore(store);
        await using var competitor = Build(handler, stale, targets, new TestTargetSelector(0));
        await Assert.ThrowsAsync<NotSupportedException>(() => competitor.LLM.Responses.CompactAsync(new() { Model = "public" }, Call(), TestContext.Current.CancellationToken));
        Assert.Equal("b", stale.ProposedTarget); Assert.Equal("a", (await store.GetAsync("session", TestContext.Current.CancellationToken))!.TargetId);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>重建 SDK 后仍在原服务器、模型和凭据上回传完整窗口，且没有自动压缩调用。</summary>
    [Fact]
    public async Task CompactThenCreate_PreservesBoundTargetAndCompleteWindow()
    {
        var store = Store(); JsonArray? returned = null;
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("b.invalid", request.RequestUri!.Host); Assert.Equal("test-key-b", request.Headers.Authorization!.Parameter);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            Assert.Equal("actual-b", body["model"]!.GetValue<string>());
            if (request.RequestUri.AbsolutePath.EndsWith("/compact", StringComparison.Ordinal)) return Json();
            Assert.True(JsonNode.DeepEquals(returned, body["input"]));
            return Json("""{"id":"resp_1","object":"response","status":"completed","output":[]}""");
        });
        var targets = new[] { Target("a"), Target("b") };
        await using var first = Build(handler, store, targets, new TestTargetSelector(1));
        var compacted = await first.LLM.Responses.CompactAsync(new() { Model = "public", Input = JsonValue.Create("all context") }, Call(), TestContext.Current.CancellationToken);
        returned = JsonNode.Parse(compacted.Output.GetRawText())!.AsArray(); var before = returned.ToJsonString();
        var binding = (await store.GetAsync("session", TestContext.Current.CancellationToken))!;
        Assert.False(await store.OwnsResponseAsync(binding, compacted.Id!, TestContext.Current.CancellationToken));
        await using var reconnected = Build(handler, store, targets, new TestTargetSelector(0));
        var created = await reconnected.LLM.Responses.CreateAsync(new() { Model = "public", Input = returned }, Call(), TestContext.Current.CancellationToken);
        Assert.Equal(compacted.Metadata, created.Metadata); Assert.Equal(before, returned.ToJsonString());
        Assert.True(await store.OwnsResponseAsync(binding, "resp_1", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reconnected.LLM.Responses.CreateAsync(new()
        { Model = "public", PreviousResponseId = compacted.Id }, Call(), TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.Calls);
    }

    /// <summary>首次压缩只在显式支持该操作的完整目标中选择。</summary>
    [Fact]
    public async Task FirstCompaction_FiltersTargetsByFeature()
    {
        using var handler = new StubHttpHandler((request, _) =>
        {
            Assert.Equal("b.invalid", request.RequestUri!.Host); return Task.FromResult(Json());
        });
        await using var client = Build(handler, targets: [Target("a", LlmTargetFeatures.None), Target("b")], selector: new TestTargetSelector(0));
        var result = await client.LLM.Responses.CompactAsync(new() { Model = "public" }, Call(), TestContext.Current.CancellationToken);
        Assert.Equal("b", result.Metadata.TargetId); Assert.Equal(1, handler.Calls);
    }

    /// <summary>已有 Session 的基础目标不支持压缩时明确失败，不能换到另一个支持的目标。</summary>
    [Fact]
    public async Task ExistingSession_DoesNotSwitchToCapableTarget()
    {
        using var handler = new StubHttpHandler((request, _) =>
        {
            Assert.Equal("a.invalid", request.RequestUri!.Host);
            return Task.FromResult(Json("""{"id":"resp_a","status":"completed","output":[]}"""));
        });
        var store = Store();
        await using var client = Build(handler, store, [Target("a", LlmTargetFeatures.None), Target("b")], new TestTargetSelector(0));
        await client.LLM.Responses.CreateAsync(new() { Model = "public", Input = JsonValue.Create("hello") }, Call(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.LLM.Responses.CompactAsync(new() { Model = "public" }, Call(), TestContext.Current.CancellationToken));
        Assert.Equal("a", (await store.GetAsync("session", TestContext.Current.CancellationToken))!.TargetId); Assert.Equal(1, handler.Calls);
    }

    /// <summary>没有压缩目标时不创建会话，也不向基础 Responses 端点试探。</summary>
    [Fact]
    public async Task MissingFeature_DoesNotBindOrSend()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json())); var store = Store();
        await using var client = Build(handler, store, [Target(features: LlmTargetFeatures.None)]);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.LLM.Responses.CompactAsync(new() { Model = "public" }, Call(), TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls); Assert.Null(await store.GetAsync("session", TestContext.Current.CancellationToken));
    }

    /// <summary>原生 previous_response_id 保持归属校验，压缩也不能借用其他 Session 的响应。</summary>
    [Fact]
    public async Task PreviousResponse_RequiresCurrentSessionOwnership()
    {
        var generation = 0;
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            if (request.RequestUri!.AbsolutePath.EndsWith("/compact", StringComparison.Ordinal))
            {
                Assert.Equal("resp_1", body["previous_response_id"]!.GetValue<string>()); Assert.False(body.ContainsKey("input")); return Json();
            }
            return Json($"{{\"id\":\"resp_{++generation}\",\"status\":\"completed\",\"output\":[]}}");
        });
        await using var client = Build(handler);
        await client.LLM.Responses.CreateAsync(new() { Model = "public" }, Call("owner"), TestContext.Current.CancellationToken);
        await client.LLM.Responses.CreateAsync(new() { Model = "public" }, Call("other"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LLM.Responses.CompactAsync(new()
        { Model = "public", PreviousResponseId = "resp_1" }, Call("other"), TestContext.Current.CancellationToken));
        await client.LLM.Responses.CompactAsync(new() { Model = "public", PreviousResponseId = "resp_1" }, Call("owner"), TestContext.Current.CancellationToken);
        Assert.Equal(3, handler.Calls);
    }

    /// <summary>找不到已有响应所属的会话时不创建新路由。</summary>
    [Fact]
    public async Task PreviousResponse_WithMissingSession_IsRejected()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json())); var store = Store();
        await using var client = Build(handler, store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LLM.Responses.CompactAsync(new()
        { Model = "public", PreviousResponseId = "resp_foreign" }, Call(), TestContext.Current.CancellationToken));
        Assert.Null(await store.GetAsync("session", TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }

    /// <summary>目标功能或实际模型变更不能借用旧会话重新绑定。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ChangedTarget_IsRejected(bool changeModel)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json())); var store = Store();
        await using var first = Build(handler, store);
        await first.LLM.Responses.CompactAsync(new() { Model = "public" }, Call(), TestContext.Current.CancellationToken);
        var changed = changeModel ? Target() with { UpstreamModel = "different" } : Target(features: LlmTargetFeatures.None);
        await using var second = Build(handler, store, [changed]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.Responses.CompactAsync(new() { Model = "public" }, Call(), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>并发首次压缩使用协调器最终胜出的完整目标，不形成不同模型/Key 的混合 Session。</summary>
    [Fact]
    public async Task ConcurrentFirstCompactions_UseWinningTarget()
    {
        using var handler = new StubHttpHandler((request, _) =>
        {
            var id = request.RequestUri!.Host.StartsWith('a') ? "a" : "b";
            Assert.Equal("test-key-" + id, request.Headers.Authorization!.Parameter); return Task.FromResult(Json());
        });
        var store = Store();
        var calls = Enumerable.Range(0, 16).Select(index => Task.Run(async () =>
        {
            await using var client = Build(handler, store, [Target("a"), Target("b")], new TestTargetSelector(index));
            return await client.LLM.Responses.CompactAsync(new() { Model = "public" }, Call(), TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken));
        var results = await Task.WhenAll(calls);
        Assert.Single(results.Select(r => r.Metadata.TargetId).Distinct()); Assert.Single(results.Select(r => r.Metadata.UpstreamModel).Distinct());
        Assert.Equal(16, handler.Calls);
    }
}
