using System.Text;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>Express 使用显式 Key/短资源身份，不能隐式借用标准模式的主体或项目。</summary>
public sealed class ExpressBindingTests
{
    /// <summary>匿名/External 在 SDK 注册和直接调用时都拒绝，不触发认证降级。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnsupportedAuthentication_IsRejectedBeforeDispatch(bool external)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ExpressFixture.Reply())); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexExpressLlmProvider(factory); var credential = external ? LlmCredential.External("identity", "principal") : LlmCredential.Anonymous("identity");
        Assert.False(provider.SupportsCredential((LlmCredentialKind)999)); Assert.Throws<ArgumentException>(() => ExpressFixture.Client(provider, credential: credential)); await Assert.ThrowsAsync<ArgumentException>(() => provider.GenerateAsync(ExpressFixture.Request(), ExpressFixture.Context(credential), TestContext.Current.CancellationToken)); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>只接受明确的 Express Google 短资源，不自动补项目、去项目或切换 Claude。</summary>
    [Theory] [InlineData(VertexFixture.Resource)] [InlineData("configured-express")] [InlineData("publishers/anthropic/models/claude")] [InlineData("publishers/google/models/..")] [InlineData("publishers/google/models/m/extra")]
    public async Task WrongResource_IsRejectedBeforeFactory(string resource)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ExpressFixture.Reply())); var factory = new VertexHttpFactory(handler); var context = new LlmAdapterContext(ExpressFixture.Target(resource: resource), new("identity", ExpressFixture.Key), "public", "session");
        await Assert.ThrowsAsync<ArgumentException>(() => new GoogleVertexExpressLlmProvider(factory).GenerateAsync(ExpressFixture.Request(), context, TestContext.Current.CancellationToken)); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>其他认证/项目默认头在完整及流式发送前失败，不修改工厂或发起重试。</summary>
    [Theory] [InlineData("Authorization", false)] [InlineData("Authorization", true)] [InlineData("api-key", false)] [InlineData("x-api-key", true)] [InlineData("x-goog-user-project", false)] [InlineData("x-goog-user-project", true)]
    public async Task UnselectedDefaultHeaders_AreRejected(string header, bool stream)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ExpressFixture.Reply())); var factory = new VertexHttpFactory(handler) { DefaultHeader = header };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExpressFixture.Direct(new(factory), stream, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>Key 中的空白/控制字符不能进入头，也不能在异常中回显。</summary>
    [Theory] [InlineData("private value")] [InlineData("private\r\nvalue")] [InlineData("private\u0000value")]
    public async Task InvalidKey_DoesNotLeakOrCreateClient(string key)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ExpressFixture.Reply())); var factory = new VertexHttpFactory(handler);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => new GoogleVertexExpressLlmProvider(factory).GenerateAsync(ExpressFixture.Request(), ExpressFixture.Context(new("identity", key)), TestContext.Current.CancellationToken)); Assert.DoesNotContain("private", error.Message); Assert.Empty(factory.Clients);
    }
    /// <summary>跨客户端完整/流式恢复同一服务器和短资源，不因选择器改变而重选目标。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Session_RestoresCompleteExpressTarget(bool firstStream)
    {
        var store = VertexFixture.Store(); var resource = ExpressFixture.Resource.Replace("configured-express", "other-express"); var count = 0;
        using var handler = new StubHttpHandler((request, _) => { count++; Assert.Equal("second.invalid", request.RequestUri!.Host); Assert.Contains("/models/other-express:", request.RequestUri.AbsolutePath); Assert.Equal(ExpressFixture.Key, Assert.Single(request.Headers.GetValues("x-goog-api-key"))); var stream = count == 1 ? firstStream : !firstStream; return Task.FromResult(stream ? StreamingTests.Reply(new OllamaBodyStream(Encoding.UTF8.GetBytes(VertexFixture.Sse()))) : ExpressFixture.Reply()); }); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexExpressLlmProvider(factory); var targets = new[] { ExpressFixture.Target(), ExpressFixture.Target("second", resource) };
        await using (var first = ExpressFixture.Client(provider, store, targets, 1)) await ExpressFixture.Invoke(first, firstStream);
        await using (var second = ExpressFixture.Client(provider, store, targets)) await ExpressFixture.Invoke(second, !firstStream);
        var binding = await store.GetAsync("express-session", TestContext.Current.CancellationToken); Assert.DoesNotContain(ExpressFixture.Key, JsonSerializer.Serialize(binding)); Assert.Equal(2, handler.Calls); Assert.All(factory.Clients, client => Assert.True(client.Disposed)); Assert.False(factory.Disposed);
    }
    /// <summary>Key、资源、服务器、版本和本地策略变更全部拒绝原 Session，不重新请求上游。</summary>
    [Theory] [InlineData("key")] [InlineData("model")] [InlineData("server")] [InlineData("version")] [InlineData("limit")] [InlineData("tail")] [InlineData("candidate")] [InlineData("client")]
    public async Task ConfigurationChange_InvalidatesSession(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ExpressFixture.Reply())); var factory = new VertexHttpFactory(handler); var store = VertexFixture.Store(); await using (var first = ExpressFixture.Client(new(factory), store)) await ExpressFixture.Invoke(first, false);
        var target = ExpressFixture.Target(); target = change switch { "model" => target with { UpstreamModel = "publishers/google/models/other" }, "server" => target with { BaseAddress = new("https://other.invalid/proxy/v1/") }, "version" => target with { Version = "2" }, _ => target };
        var options = new GoogleVertexExpressLlmOptions { MaximumBodyBytes = change == "limit" ? 1024 : 32 * 1024 * 1024, MaximumBufferedTerminalEvents = change == "tail" ? 1 : 128, MaximumTrackedCandidates = change == "candidate" ? 1 : 128 }; var provider = new GoogleVertexExpressLlmProvider(factory, options, httpClientName: change == "client" ? "other-client" : "Asgard.Loki.LLM.GoogleVertex.Express"); await using var next = ExpressFixture.Client(provider, store, [target], credential: change == "key" ? new("identity", "changed-key") : null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExpressFixture.Invoke(next, true)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>Express 不暴露 Claude 适配器，错误协议注册在首次缓存和 HTTP 之前失败。</summary>
    [Fact]
    public void ClaudeProtocol_IsRejectedAtRegistration()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ExpressFixture.Reply())); var store = VertexFixture.Store(); Assert.Throws<ArgumentException>(() => ExpressFixture.Client(new(new VertexHttpFactory(handler)), store, [ExpressFixture.Target() with { Protocols = LlmProtocol.ClaudeMessages }])); Assert.Equal(0, store.GetOrCreateCalls); Assert.Equal(0, handler.Calls);
    }
}
