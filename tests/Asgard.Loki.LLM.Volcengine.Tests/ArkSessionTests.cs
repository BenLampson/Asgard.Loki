using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Volcengine.Tests.ArkFixture;

namespace Asgard.Loki.LLM.Volcengine.Tests;

/// <summary>新原生终态语义、命名客户端和资源策略都属于完整模型目标的持久 Session 绑定。</summary>
public sealed class ArkSessionTests
{
    /// <summary>每个资源策略、根地址、模型或命名客户端变化都不能复用旧目标绑定。</summary>
    [Theory] [InlineData("request")] [InlineData("body")] [InlineData("tail")] [InlineData("choices")] [InlineData("client")] [InlineData("root")] [InlineData("model")]
    public async Task ChangedPolicyOrTarget_FailsBeforeSecondHttp(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(Part("responses_result").GetRawText())), false))); using var factory = new ArkHttpFactory(handler); var store = Store();
        await using (var first = Client(new(factory), store)) await first.LLM.Responses.CreateAsync(new() { Model = "public" }, new() { SessionId = "ark-session" }, TestContext.Current.CancellationToken);
        var policy = change switch { "request" => new VolcengineHttpOptions { MaximumRequestBytes = 1000 }, "body" => new() { MaximumBodyBytes = 1000 }, "tail" => new() { MaximumBufferedTerminalEvents = 2 }, "choices" => new() { MaximumTrackedChoices = 2 }, _ => new() };
        var target = change == "root" ? Target() with { BaseAddress = new("https://other.invalid/api/v3/") } : change == "model" ? Target() with { UpstreamModel = "other" } : Target();
        await using var second = Client(new VolcengineLlmProvider(factory, policy, httpClientName: change == "client" ? "other" : "Asgard.Loki.LLM.Volcengine"), store, target);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.Responses.CreateAsync(new() { Model = "public" }, new() { SessionId = "ark-session" }, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls); Assert.Single(factory.Clients);
    }
    /// <summary>相同完整配置可以跨实例恢复，响应归属仍绑定服务器/实际模型/凭据，JSON 保持原样。</summary>
    [Fact]
    public async Task SameConfig_RecreatesAndPreservesResponseOwnership()
    {
        var calls = 0; using var handler = new StubHttpHandler(async (message, token) => { if (calls++ != 0) { var data = JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token))!.AsObject(); Assert.Equal("response-ark-a", data["previous_response_id"]!.GetValue<string>()); } return Reply(new MemoryStream(Bytes(Part("responses_result").GetRawText())), false); });
        using var factory = new ArkHttpFactory(handler); var store = Store(); await using (var first = Client(new(factory), store)) await first.LLM.Responses.CreateAsync(new() { Model = "public" }, new() { SessionId = "ark-session" }, TestContext.Current.CancellationToken);
        await using (var second = Client(new(factory), store)) { var result = await second.LLM.Responses.CreateAsync(new() { Model = "public", PreviousResponseId = "response-ark-a" }, new() { SessionId = "ark-session" }, TestContext.Current.CancellationToken); Assert.Equal("configured-model", result.Metadata.UpstreamModel); Assert.Equal("ark-native", result.Metadata.TargetId); }
        Assert.Equal(2, handler.Calls); Assert.False(factory.Disposed);
    }
    /// <summary>保留旧构造签名；新显式 HTTP 域不会冒充旧未检查 EOF 的默认适配身份。</summary>
    [Fact]
    public void NativePolicyAndLegacySignature_AreExplicit()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); using var factory = new ArkHttpFactory(handler); var provider = new VolcengineLlmProvider(factory);
        Assert.NotNull(typeof(VolcengineLlmProvider).GetConstructor([typeof(IHttpClientFactory), typeof(string), typeof(string)])); Assert.Contains("ark-http-v2", provider.BindingIdentity); Assert.DoesNotContain(Key, provider.BindingIdentity); Assert.NotEqual(typeof(VolcengineLlmProvider).AssemblyQualifiedName, provider.BindingIdentity); Assert.Equal(provider.BindingIdentity, new VolcengineLlmProvider(factory, new VolcengineHttpOptions()).BindingIdentity); Assert.Empty(factory.Clients);
    }
}
