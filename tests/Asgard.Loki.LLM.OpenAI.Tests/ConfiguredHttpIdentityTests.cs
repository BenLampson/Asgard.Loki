using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;
using static Asgard.Loki.LLM.OpenAI.Tests.ConfiguredHttpFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>实际选中目标和认证不能被命名客户端或处理器暗中替换。</summary>
public sealed class ConfiguredHttpIdentityTests
{
    /// <summary>每种显式位置都拒绝其他默认认证头，而不更改工厂配置。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task UnselectedDefaultAuth_IsRejected(int profile)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); var factory = new OpenAiHttpFactory(handler) { Configure = client => client.DefaultRequestHeaders.Add("x-goog-api-key", "unselected-key") }; await using var sdk = Build(factory, "chat", profile); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(sdk, "chat")); Assert.Equal("unselected-key", Assert.Single(Assert.Single(factory.Clients).DefaultRequestHeaders.GetValues("x-goog-api-key"))); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(0, handler.Calls);
    }
    /// <summary>处理器或客户端关闭回调改写路径、Key 或方法不能成功交付原生窗口。</summary>
    public static IEnumerable<object[]> Mutations()
    { foreach (var mode in Modes) foreach (var field in new[] { "url", "method", "key", "late" }) yield return [mode, field]; }
    /// <summary>初次响应和全部拥有资源关闭后均复查实际请求，不接受隐藏转发。</summary>
    [Theory] [MemberData(nameof(Mutations))]
    public async Task ActualWireMutation_PreventsDelivery(string mode, string field)
    {
        HttpRequestMessage? actual = null; var body = new OllamaBodyStream(Bytes(Part(mode).GetProperty("result").GetRawText()));
        using var handler = new StubHttpHandler((message, _) =>
        {
            actual = message;
            if (field == "url") message.RequestUri = new("https://other.invalid/run"); else if (field == "method") message.Method = HttpMethod.Get; else if (field == "key") { message.Headers.Remove("x-native-key"); message.Headers.Add("x-native-key", "changed-key"); }
            return Task.FromResult(Reply(body, false));
        }); var factory = new OpenAiHttpFactory(handler) { OnDispose = () => { if (field == "late") actual!.Headers.Host = "changed.invalid"; } }; await using var sdk = Build(factory, mode, 2); await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, mode)); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>固定重复业务查询保持原字节，认证名和密钥仅各转义一次。</summary>
    [Fact]
    public async Task FixedQueriesAndEscapedCredential_ArePreserved()
    {
        using var handler = new StubHttpHandler((message, _) => { Assert.Equal("https://configured.invalid/deployment/api/native.run?tag=1&tag=2&empty=&a%2Fb=k%2F%2B%3F%26%3D%25%E9%9B%AA", message.RequestUri!.OriginalString); return Task.FromResult(Reply(new OllamaBodyStream(Bytes(Part("chat").GetProperty("result").GetRawText())), false)); }); var factory = new OpenAiHttpFactory(handler); var transport = Options(3) with { ChatCompletionsPath = "/native.run?tag=1&tag=2&empty=", CredentialName = "a/b", CredentialPrefix = "", CredentialSuffix = "" }; await using var sdk = Build(factory, "chat", 3, transport: transport); await Invoke(sdk, "chat"); Assert.Equal(1, handler.Calls);
    }
    /// <summary>认证位置不能绕过压缩显式授权，或把压缩结果 ID 变成生成响应归属。</summary>
    [Fact]
    public async Task Compact_FeatureAndOwnershipRemainExplicit()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Part("compact").GetProperty("result").GetRawText())), false))); var factory = new OpenAiHttpFactory(handler); await using (var missing = Build(factory, "compact", 1, target: Target("compact") with { Features = LlmTargetFeatures.None })) await Assert.ThrowsAsync<NotSupportedException>(() => Invoke(missing, "compact")); Assert.Equal(0, handler.Calls);
        await using var sdk = Build(factory, "compact", 1); await Invoke(sdk, "compact"); await Assert.ThrowsAsync<InvalidOperationException>(() => sdk.LLM.Responses.CreateAsync(new() { Model = "public", PreviousResponseId = "cp-native", Input = JsonSerializer.SerializeToNode("next") }, new() { SessionId = "configured-http" }, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>固定查询中的普通 URL 值不是改换目标，认证仍独立追加和一次转义。</summary>
    [Fact]
    public async Task UrlValueInFixedQuery_IsNotAnEndpointOverride()
    {
        using var handler = new StubHttpHandler((message, _) => { Assert.Equal("https://configured.invalid/deployment/api/native.run?reference=https://example.invalid/a&api_key=key%3Dk%2F%2B%3F%26%3D%25%E9%9B%AA%2Fok", message.RequestUri!.OriginalString); return Task.FromResult(Reply(new OllamaBodyStream(Bytes(Part("chat").GetProperty("result").GetRawText())), false)); }); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "chat", 3, transport: Options(3) with { ChatCompletionsPath = "native.run?reference=https://example.invalid/a" }); await Invoke(sdk, "chat"); Assert.Equal(1, handler.Calls);
    }
    /// <summary>自定义 Authorization 字段保留明确前缀，不自动修复空白或改为 Bearer。</summary>
    [Fact]
    public async Task ExplicitAuthorizationHeader_IsNotNormalized()
    {
        using var handler = new StubHttpHandler((message, _) => { Assert.Equal("Token  fixture-header/suffix", Assert.Single(message.Headers.NonValidated["Authorization"])); return Task.FromResult(Reply(new OllamaBodyStream(Bytes(Part("chat").GetProperty("result").GetRawText())), false)); }); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "chat", 2, transport: Options(2) with { CredentialName = "Authorization", CredentialPrefix = "Token  " }); await Invoke(sdk, "chat"); Assert.Equal(1, handler.Calls);
    }
    /// <summary>显式无认证也拒绝命名客户端中的未选中 Cookie。</summary>
    [Fact]
    public async Task Anonymous_DoesNotBorrowCookieAuthentication()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); var factory = new OpenAiHttpFactory(handler) { Configure = client => client.DefaultRequestHeaders.Add("Cookie", "session=unselected") }; await using var sdk = Build(factory, "chat", 1); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(sdk, "chat")); Assert.Equal(0, handler.Calls); Assert.Equal("session=unselected", Assert.Single(Assert.Single(factory.Clients).DefaultRequestHeaders.GetValues("Cookie")));
    }
    /// <summary>三个原生 SSE 操作在消费者提前退出时只关闭本次调用。</summary>
    public static IEnumerable<object[]> EarlyCases()
    { foreach (var mode in new[] { "chat", "responses", "text" }) foreach (var profile in Enumerable.Range(0, 4)) yield return [mode, profile]; }
    /// <summary>不继续读取剩余事件，不保留后台任务，也不重试。</summary>
    [Theory] [MemberData(nameof(EarlyCases))]
    public async Task EarlyExit_ClosesOwnedResources(string mode, int profile)
    {
        var body = new OllamaBodyStream(Bytes(Part(mode).GetProperty("sse").GetString()!)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, mode, profile); var call = new LlmCallOptions { SessionId = "configured-http" }; var token = TestContext.Current.CancellationToken;
        if (mode == "chat") { await foreach (var item in sdk.LLM.ChatCompletions.StreamAsync((ChatCompletionRequest)Request(mode), call, token)) { Assert.False(Ending(item)); break; } }
        else if (mode == "responses") { await foreach (var item in sdk.LLM.Responses.StreamAsync((ResponseRequest)Request(mode), call, token)) { Assert.False(Ending(item)); break; } }
        else { await foreach (var item in sdk.LLM.TextCompletions.StreamAsync((TextCompletionRequest)Request(mode), call, token)) { Assert.False(Ending(item)); break; } }
        Assert.False(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>HTTP 无认证不改变原生双工认证，也不打开任何默认套接字。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AnonymousHttp_DoesNotEnableAnonymousWebSocket(bool realtime)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected HTTP")); var factory = new OpenAiHttpFactory(handler); var provider = new OpenAiLlmProvider(factory, new OpenAiHttpOptions { Transport = Options(1) }); var context = new LlmAdapterContext(Target("chat"), LlmCredential.Anonymous("selected-key"), "public", "session");
        await Assert.ThrowsAsync<NotSupportedException>(async () => { if (realtime) await using (var connection = await provider.OpenRealtimeAsync(context, TestContext.Current.CancellationToken)) { } else await using (var connection = await provider.OpenWebSocketAsync(context, TestContext.Current.CancellationToken)) { } }); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
}
