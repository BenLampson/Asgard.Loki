using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Ollama.Tests;

/// <summary>generate 传输分支、认证和调用者拥有的 context 回传，不依赖 Chat 测试替代验证。</summary>
public sealed class GenerateBoundaryTests
{
    /// <summary>完整/流式分支都检查字节数、声明长度与媒体类型。</summary>
    [Theory] [InlineData(false, "limit")] [InlineData(true, "limit")] [InlineData(false, "exact")] [InlineData(true, "exact")]
    [InlineData(false, "short")] [InlineData(true, "short")] [InlineData(false, "long")] [InlineData(true, "long")]
    [InlineData(false, "media")] [InlineData(true, "media")]
    public async Task GenerateTransportBounds_AreEnforced(bool stream, string failure)
    {
        var bytes = Encoding.UTF8.GetBytes(GenerateTerminalTests.End); var body = new OllamaBodyStream(bytes);
        using var handler = new StubHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new(failure == "media" ? "text/event-stream" : stream ? "application/x-ndjson" : "application/json");
            if (failure is "short" or "long") response.Content.Headers.ContentLength = bytes.Length + (failure == "short" ? 1 : -1); return Task.FromResult(response);
        });
        var limit = failure == "limit" ? bytes.Length - 1 : bytes.Length;
        var provider = new OllamaLlmProvider(new StubHttpClientFactory(handler), options: new() { MaximumResponseBytes = limit, MaximumStreamRecordBytes = limit }); var items = new List<OllamaGenerateEvent>();
        async Task Invoke()
        { if (stream) { await foreach (var item in ((IOllamaGenerateAdapter)provider).StreamAsync(GenerateTerminalTests.Payload(), GenerateTerminalTests.Context(), TestContext.Current.CancellationToken)) items.Add(item); } else await provider.GenerateAsync(GenerateTerminalTests.Payload(), GenerateTerminalTests.Context(), TestContext.Current.CancellationToken); }
        if (failure == "exact") await Invoke(); else { await Assert.ThrowsAsync<InvalidDataException>(Invoke); Assert.Empty(items); }
        Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>两个 generate 分支都不能继承匿名目标未选中的默认 Key。</summary>
    [Theory] [InlineData(false, true)] [InlineData(false, false)] [InlineData(true, true)] [InlineData(true, false)]
    public async Task GenerateAuthentication_KeepsExplicitIdentity(bool stream, bool anonymous)
    {
        using var handler = new StubHttpHandler((message, _) =>
        { Assert.Equal("Bearer fixture-secret", message.Headers.Authorization!.ToString()); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(GenerateTerminalTests.End, Encoding.UTF8, "application/json") }); });
        var factory = new OllamaHttpFactory(handler) { DefaultBearer = "unselected" }; var provider = new OllamaLlmProvider(factory);
        var context = new LlmAdapterContext(GenerateTerminalTests.Context().Target, OllamaFixture.Credential(anonymous), "public", "session");
        async Task Invoke()
        { if (stream) { await foreach (var _ in ((IOllamaGenerateAdapter)provider).StreamAsync(GenerateTerminalTests.Payload(), context, TestContext.Current.CancellationToken)) { } } else await provider.GenerateAsync(GenerateTerminalTests.Payload(), context, TestContext.Current.CancellationToken); }
        if (anonymous) await Assert.ThrowsAsync<InvalidOperationException>(Invoke); else await Invoke();
        Assert.Equal(anonymous ? 0 : 1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>提前退出 generate 不读到 done，不自动恢复或重试。</summary>
    [Fact]
    public async Task EarlyGenerateDisposal_ReleasesResources()
    {
        var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(GenerateTerminalTests.Partial + "\n" + GenerateTerminalTests.End)); using var handler = GenerateTerminalTests.Handler(body, true); var factory = new OllamaHttpFactory(handler);
        IOllamaGenerateAdapter provider = new OllamaLlmProvider(factory);
        await using (var iterator = provider.StreamAsync(GenerateTerminalTests.Payload(), GenerateTerminalTests.Context(), TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        { Assert.True(await iterator.MoveNextAsync()); Assert.False(iterator.Current.IsTerminal); Assert.False(body.Disposed); }
        Assert.True(body.Disposed); Assert.False(body.Eof); Assert.True(factory.Clients[0].Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>generate context 不被 SDK 自动保存或回填，仅按调用者显式数据发送。</summary>
    [Fact]
    public async Task ContextRemainsCallerOwned()
    {
        var calls = 0; using var handler = new StubHttpHandler(async (message, token) =>
        {
            var wire = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!.AsObject(); Assert.Equal(calls == 2, wire.ContainsKey("context"));
            if (calls == 2) Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[42,0,42]"), wire["context"])); calls++;
            return new(HttpStatusCode.OK) { Content = new StringContent(GenerateTerminalTests.End[..^1] + ",\"context\":[42,0,42]}", Encoding.UTF8, "application/json") };
        });
        await using var client = OllamaFixture.Client(new(new StubHttpClientFactory(handler)), targets: [OllamaFixture.Target() with { Protocols = LlmProtocol.OllamaGenerate }]);
        var request = new OllamaGenerateRequest { Model = "public" }; var options = new LlmCallOptions { SessionId = "context" };
        var first = await client.LLM.OllamaGenerate.GenerateAsync(request, options, TestContext.Current.CancellationToken);
        await client.LLM.OllamaGenerate.GenerateAsync(request, options, TestContext.Current.CancellationToken);
        request.Parameters["context"] = first.Data.GetProperty("context"); await client.LLM.OllamaGenerate.GenerateAsync(request, options, TestContext.Current.CancellationToken); Assert.Equal(3, calls);
    }
    /// <summary>预先取消不创建客户端，两个 generate 分支都遵守。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task GeneratePreCancellation_DoesNotDispatch(bool stream)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); cancellation.Cancel(); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected HTTP"));
        var factory = new OllamaHttpFactory(handler); var provider = new OllamaLlmProvider(factory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { if (stream) { await foreach (var _ in ((IOllamaGenerateAdapter)provider).StreamAsync(GenerateTerminalTests.Payload(), GenerateTerminalTests.Context(), cancellation.Token)) { } } else await provider.GenerateAsync(GenerateTerminalTests.Payload(), GenerateTerminalTests.Context(), cancellation.Token); });
        Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>单独 Chat 适配器不能冒充 generate；注册必须校验原生接口。</summary>
    [Fact]
    public void GenerateTarget_RequiresGenerateAdapter()
    {
        Assert.Throws<ArgumentException>(() => new LokiClientBuilder().AddLLM(new LlmOptions { Providers = [new ChatOnlyAdapter()], Credentials = [new("identity", "fixture-secret")], SessionStore = OllamaFixture.Store(),
            Routes = [new() { Model = "public", Targets = [OllamaFixture.Target() with { Protocols = LlmProtocol.OllamaGenerate }] }] }));
    }
}
