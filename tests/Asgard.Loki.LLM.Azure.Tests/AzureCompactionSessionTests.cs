using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>压缩沿用既有完整绑定和响应归属；新增资源策略省略时保持历史指纹字节。</summary>
public sealed class AzureCompactionSessionTests
{
    /// <summary>默认新策略不改变旧配置序列化字节；显式设置即进入绑定。</summary>
    [Fact]
    public void DefaultBindingRemainsCompatible()
    {
        using var factory = AzureCompactionFixture.Factory(); var provider = new AzureLlmProvider(factory);
        var expected = provider.GetType().AssemblyQualifiedName + ":azure-sse-v1:{\"Options\":{\"ApiStyle\":0,\"ApiVersion\":null,\"MaximumBodyBytes\":33554432,\"MaximumBufferedTerminalEvents\":128,\"MaximumTrackedChoices\":128},\"Client\":\"Asgard.Loki.LLM.Azure\",\"Source\":null}";
        Assert.Equal(expected, provider.BindingIdentity); Assert.DoesNotContain("MaximumCompactionRequestBytes", JsonSerializer.Serialize(new AzureLlmOptions()));
        var explicitPolicy = new AzureLlmProvider(factory, new() { MaximumCompactionRequestBytes = 32 * 1024 * 1024 }); Assert.NotEqual(expected, explicitPolicy.BindingIdentity); Assert.Contains("\"MaximumCompactionRequestBytes\":33554432", explicitPolicy.BindingIdentity); Assert.Equal(0, factory.Creates);
    }
    /// <summary>非正请求上限在构造时失败。</summary>
    [Theory] [InlineData(0)] [InlineData(-1)]
    public void InvalidPolicyFailsBeforeFactory(int maximum)
    { using var factory = AzureCompactionFixture.Factory(); Assert.Throws<ArgumentOutOfRangeException>(() => new AzureLlmProvider(factory, new() { MaximumCompactionRequestBytes = maximum })); Assert.Equal(0, factory.Creates); }
    /// <summary>整个返回窗口可供下一次显式生成使用，重建和目标重排不改绑。</summary>
    [Fact]
    public async Task RebuiltClientUsesOriginalTargetAndCompleteWindow()
    {
        using var factory = AzureCompactionFixture.Factory(); var store = AzureFixture.WebSocketStore(); var a = AzureCompactionFixture.Target(); var b = a with { Id = "second", BaseAddress = new("https://second.invalid/proxy/openai/v1/"), UpstreamModel = "other.deployment" }; JsonArray? output = null;
        factory.OnSend = async (request, token) =>
        {
            Assert.Equal("azure-compact.example.invalid", request.RequestUri!.Host); Assert.Equal("fixture-key", Assert.Single(request.Headers.GetValues("api-key")));
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject(); Assert.Equal("deploy.compact.1", body["model"]!.GetValue<string>());
            if (request.RequestUri.AbsolutePath.EndsWith("/compact", StringComparison.Ordinal)) return AzureCompactionFixture.Json(AzureCompactionFixture.Part("response"));
            Assert.True(JsonNode.DeepEquals(output, body["input"])); return AzureCompactionFixture.SeedReply();
        };
        ResponseCompaction compact;
        await using (var first = AzureFixture.Client(new AzureLlmProvider(factory), true, store: store, targets: [a, b])) compact = await first.LLM.Responses.CompactAsync(AzureCompactionFixture.Request(), AzureCompactionFixture.Call(), TestContext.Current.CancellationToken);
        output = JsonNode.Parse(compact.Output.GetRawText())!.AsArray(); var before = output.ToJsonString(); var binding = await store.GetAsync("compact-session", TestContext.Current.CancellationToken); Assert.NotNull(binding);
        Assert.False(await store.OwnsResponseAsync(binding, compact.Id!, TestContext.Current.CancellationToken));
        await using var rebuilt = AzureFixture.Client(new AzureLlmProvider(factory), true, store: store, targets: [b, a]);
        var generated = await rebuilt.LLM.Responses.CreateAsync(new() { Model = "public", Input = output }, AzureCompactionFixture.Call(), TestContext.Current.CancellationToken);
        Assert.Equal(compact.Metadata, generated.Metadata); Assert.Equal(before, output.ToJsonString()); Assert.True(await store.OwnsResponseAsync(binding, "resp-owned-fixture", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rebuilt.LLM.Responses.CompactAsync(new() { Model = "public", PreviousResponseId = compact.Id }, AzureCompactionFixture.Call(), TestContext.Current.CancellationToken)); Assert.Equal(2, factory.Calls); Assert.False(factory.Disposed);
    }
    /// <summary>未声明压缩能力时首次调用不创建绑定；既有会话也不能切换至支持目标。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MissingFeatureDoesNotProbeOrRebind(bool bound)
    {
        using var factory = AzureCompactionFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(AzureCompactionFixture.SeedReply()); var store = AzureFixture.WebSocketStore(); var a = AzureCompactionFixture.Target(features: LlmTargetFeatures.None); var b = AzureCompactionFixture.Target() with { Id = "second" };
        await using var client = AzureFixture.Client(new AzureLlmProvider(factory), true, store: store, targets: bound ? [a, b] : [a]); if (bound) await AzureCompactionFixture.SeedAsync(client, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.LLM.Responses.CompactAsync(AzureCompactionFixture.Request(), AzureCompactionFixture.Call(), TestContext.Current.CancellationToken));
        Assert.Equal(bound ? 1 : 0, factory.Calls); var binding = await store.GetAsync("compact-session", TestContext.Current.CancellationToken); if (bound) Assert.Equal("primary", binding!.TargetId); else Assert.Null(binding);
    }
    /// <summary>压缩引用必须由当前完整 Session 拥有，不能凭响应 ID 自动查找或创建目标。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ForeignOrMissingPreviousResponseCannotSend(bool existing)
    {
        using var factory = AzureCompactionFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(AzureCompactionFixture.SeedReply()); var store = AzureFixture.WebSocketStore();
        await using var client = AzureCompactionFixture.Build(new AzureLlmProvider(factory), store: store); if (existing) await AzureCompactionFixture.SeedAsync(client, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LLM.Responses.CompactAsync(new() { Model = "public", PreviousResponseId = "resp-foreign" }, AzureCompactionFixture.Call(), TestContext.Current.CancellationToken)); Assert.Equal(existing ? 1 : 0, factory.Calls);
    }
    /// <summary>真实归属其他 Session 的生成 ID 不允许用于本 Session 压缩。</summary>
    [Fact]
    public async Task ResponseOwnedByAnotherSessionCannotBeBorrowed()
    {
        using var factory = AzureCompactionFixture.Factory(); var generation = 0; factory.OnSend = (_, _) => Task.FromResult(AzureCompactionFixture.Json(AzureCompactionFixture.Element("{\"id\":\"resp-" + ++generation + "\",\"status\":\"completed\",\"output\":[]}")));
        await using var client = AzureCompactionFixture.Build(new AzureLlmProvider(factory)); var token = TestContext.Current.CancellationToken;
        await client.LLM.Responses.CreateAsync(new() { Model = "public" }, AzureCompactionFixture.Call("owner"), token); await client.LLM.Responses.CreateAsync(new() { Model = "public" }, AzureCompactionFixture.Call("other"), token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LLM.Responses.CompactAsync(new() { Model = "public", PreviousResponseId = "resp-1" }, AzureCompactionFixture.Call("other"), token)); Assert.Equal(2, factory.Calls);
    }
    /// <summary>实际模型、根地址、能力或显式策略变化使旧绑定失效，不重新分配。</summary>
    [Theory] [InlineData("model")] [InlineData("root")] [InlineData("feature")] [InlineData("policy")] [InlineData("removed")]
    public async Task ChangedCompleteBindingFailsBeforeSecondHttp(string kind)
    {
        using var factory = AzureCompactionFixture.Factory(); var store = AzureFixture.WebSocketStore();
        await using (var first = AzureCompactionFixture.Build(new AzureLlmProvider(factory), store: store)) await first.LLM.Responses.CompactAsync(AzureCompactionFixture.Request(), AzureCompactionFixture.Call(), TestContext.Current.CancellationToken);
        var target = AzureCompactionFixture.Target(); target = kind switch
        { "model" => target with { UpstreamModel = "different" }, "root" => target with { BaseAddress = new("https://different.invalid/openai/v1/") }, "feature" => target with { Features = LlmTargetFeatures.None }, "removed" => target with { Id = "replacement" }, _ => target };
        var provider = new AzureLlmProvider(factory, kind == "policy" ? new() { MaximumCompactionRequestBytes = 32 * 1024 * 1024 } : null);
        await using var second = AzureCompactionFixture.Build(provider, store: store, targets: [target]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.Responses.CompactAsync(AzureCompactionFixture.Request(), AzureCompactionFixture.Call(), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls);
    }
}
