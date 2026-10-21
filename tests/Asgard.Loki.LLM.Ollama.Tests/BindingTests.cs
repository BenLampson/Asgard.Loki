using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Ollama.Tests;

/// <summary>新协议复用完整 Session 与调用快照，不把匿名服务器变成无身份的轮询池。</summary>
public sealed class BindingTests
{
    /// <summary>公共调用在等待缓存前固定消息和参数，异步流在开始枚举时固定。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RequestSnapshot_PrecedesAwaitedSession(bool stream)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ControlledSessionStore(OllamaFixture.Store()) { BeforeRead = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var expected = JsonNode.Parse(OllamaFixture.Text("wire"))!; expected["stream"] = stream;
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))));
            return new(HttpStatusCode.OK) { Content = new StringContent(OllamaFixture.Terminal, System.Text.Encoding.UTF8, "application/json") };
        });
        await using var client = OllamaFixture.Client(new(new StubHttpClientFactory(handler)), store: store);
        var request = OllamaFixture.Request();
        async Task Invoke()
        {
            if (stream) { await foreach (var _ in client.LLM.OllamaChat.StreamAsync(request, new() { SessionId = "snapshot" }, TestContext.Current.CancellationToken)) { } }
            else await client.LLM.OllamaChat.CompleteAsync(request, new() { SessionId = "snapshot" }, TestContext.Current.CancellationToken);
        }
        var call = Invoke(); await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        request.Messages.Clear(); request.Parameters.Clear(); release.TrySetResult(); await call; Assert.Equal(1, handler.Calls);
    }
    /// <summary>缓存绑定跨客户端选择同一实际服务器和模型，第二次不重选。</summary>
    [Fact]
    public async Task NativeSession_KeepsWholeTarget()
    {
        var store = OllamaFixture.Store(); var targets = new[] { OllamaFixture.Target(), OllamaFixture.Target() with { Id = "second", BaseAddress = new("http://second.invalid/api/"), UpstreamModel = "second-model" } };
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("http://second.invalid/api/chat", message.RequestUri!.AbsoluteUri); Assert.Equal("second-model", JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!["model"]!.GetValue<string>());
            Assert.Null(message.Headers.Authorization); return new(HttpStatusCode.OK) { Content = new StringContent(OllamaFixture.Terminal, System.Text.Encoding.UTF8, "application/json") };
        });
        var provider = new OllamaLlmProvider(new StubHttpClientFactory(handler));
        await using (var first = OllamaFixture.Client(provider, store: store, targets: targets, selected: 1)) await first.LLM.OllamaChat.CompleteAsync(OllamaFixture.Request(), new() { SessionId = "shared" }, TestContext.Current.CancellationToken);
        await using (var second = OllamaFixture.Client(provider, store: store, targets: targets))
            await foreach (var _ in second.LLM.OllamaChat.StreamAsync(OllamaFixture.Request(), new() { SessionId = "shared" }, TestContext.Current.CancellationToken)) { }
        Assert.Equal(2, handler.Calls);
    }
    /// <summary>资源上限、客户端名、认证模式和目标变更都会拒绝旧 Session，而非切换或重试。</summary>
    [Theory] [InlineData("limits")] [InlineData("name")] [InlineData("credential")] [InlineData("model")] [InlineData("server")] [InlineData("version")]
    public async Task ChangedBinding_FailsBeforeDispatch(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OllamaFixture.Terminal, System.Text.Encoding.UTF8, "application/json") }));
        var factory = new StubHttpClientFactory(handler); var store = OllamaFixture.Store();
        await using (var first = OllamaFixture.Client(new(factory), store: store)) await first.LLM.OllamaChat.CompleteAsync(OllamaFixture.Request(), new() { SessionId = "fixed" }, TestContext.Current.CancellationToken);
        var provider = new OllamaLlmProvider(factory, httpClientName: change == "name" ? "other" : "Asgard.Loki.LLM.Ollama", options: change == "limits" ? new() { MaximumStreamRecordBytes = 1 } : null);
        var target = change switch { "server" => OllamaFixture.Target() with { BaseAddress = new("http://other.invalid/api/") }, "model" => OllamaFixture.Target() with { UpstreamModel = "other" }, "version" => OllamaFixture.Target() with { Version = "new" }, _ => OllamaFixture.Target() };
        await using var next = OllamaFixture.Client(provider, anonymous: change != "credential", store: store, targets: [target]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => next.LLM.OllamaChat.CompleteAsync(OllamaFixture.Request(), new() { SessionId = "fixed" }, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生 Ollama 不能伪装成 OpenAI Chat 协议。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.Gemini)] [InlineData(LlmProtocol.ClaudeMessages)]
    public void WrongProtocol_IsRejected(LlmProtocol protocol)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected HTTP"));
        Assert.Throws<ArgumentException>(() => OllamaFixture.Client(new(new StubHttpClientFactory(handler)), targets: [OllamaFixture.Target() with { Protocols = protocol }])); Assert.Equal(0, handler.Calls);
    }
    /// <summary>核心字段冲突在缓存等待和 HTTP 前失败，stream 则由方法决定。</summary>
    [Theory] [InlineData("model")] [InlineData("messages")]
    public async Task ReservedParameter_IsRejected(string name)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected HTTP"));
        await using var client = OllamaFixture.Client(new(new StubHttpClientFactory(handler))); var request = OllamaFixture.Request(); request.Parameters[name] = JsonSerializer.Deserialize<JsonElement>("null");
        await Assert.ThrowsAsync<ArgumentException>(() => client.LLM.OllamaChat.CompleteAsync(request, new() { SessionId = "reserved" }, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }
}
