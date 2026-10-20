using System.Net.WebSockets;
using System.Text;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>固定完整 Realtime 目标、凭据和配置，缓存不能恢复云端连接或自动重放事件。</summary>
public sealed class AzureRealtimeSessionTests
{
    /// <summary>显式重连时令牌可旋转，但目标重排不能移动绑定，新的 socket 没有自动指令。</summary>
    [Fact]
    public async Task ExplicitReopenRetainsTargetWithRotatingTokenAndNoReplay()
    {
        var clock = new AzureClock(); using var source = new AzureTokenSource(clock); using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory(); var store = AzureFixture.WebSocketStore(); var a = AzureRealtimeFixture.Target(); var b = a with { Id = "other", BaseAddress = new("https://other.invalid/openai/v1/"), UpstreamModel = "other-model" }; LlmResultMetadata original;
        await using (var first = AzureRealtimeFixture.Build(AzureRealtimeFixture.Provider(http, factory, source: source, clock: clock), true, store: store, targets: [a, b]))
        { await using var connection = await first.LLM.Realtime.OpenAsync("public", AzureRealtimeFixture.Call(), TestContext.Current.CancellationToken); original = connection.Metadata; }
        clock.Now = clock.Now.AddHours(1);
        await using var second = AzureRealtimeFixture.Build(AzureRealtimeFixture.Provider(http, factory, source: source, clock: clock), true, store: store, targets: [b, a]);
        await using var next = await second.LLM.Realtime.OpenAsync("public", AzureRealtimeFixture.Call(), TestContext.Current.CancellationToken); Assert.Equal(original, next.Metadata);
        var handshakes = factory.Calls.ToArray(); Assert.Equal(2, handshakes.Length); Assert.Equal(handshakes[0].Address, handshakes[1].Address); Assert.Equal("Bearer fixture-token-1", handshakes[0].Headers["Authorization"]); Assert.Equal("Bearer fixture-token-2", handshakes[1].Headers["Authorization"]);
        Assert.Equal(2, source.Calls); Assert.All(factory.Sockets, socket => Assert.Empty(socket.Sent)); Assert.Equal(1, store.GetOrCreateCalls); Assert.Equal(0, http.Creates); Assert.Equal(0, factory.Disposals);
    }
    /// <summary>完整目标或适配配置改变后旧 Session 在新的握手和认证之前失败。</summary>
    [Theory] [InlineData("root")] [InlineData("model")] [InlineData("removed")] [InlineData("protocol")]
    [InlineData("principal")] [InlineData("key")] [InlineData("source")] [InlineData("factory")] [InlineData("limit")]
    public async Task ChangedBindingCannotOpenAnotherTarget(string kind)
    {
        var external = kind is "principal" or "source"; var clock = new AzureClock(); using var source = new AzureTokenSource(clock); using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory(); var store = AzureFixture.WebSocketStore(); var target = AzureRealtimeFixture.Target();
        await using (var first = AzureRealtimeFixture.Build(AzureRealtimeFixture.Provider(http, factory, source: source, clock: clock), external, store: store)) { await using var connection = await first.LLM.Realtime.OpenAsync("public", AzureRealtimeFixture.Call(), TestContext.Current.CancellationToken); }
        target = kind switch { "root" => target with { BaseAddress = new("https://other.invalid/openai/v1/") }, "model" => target with { UpstreamModel = "other" }, "removed" => target with { Id = "replacement" }, "protocol" => target with { Protocols = LlmProtocol.ChatCompletions }, _ => target };
        if (kind == "source") source.Version = "changed"; if (kind == "factory") factory.Version = "changed"; var credentials = kind == "principal" ? AzureFixture.Credential(true, "other-principal") : kind == "key" ? new LlmCredential("identity", "different-key") : AzureFixture.Credential(external);
        await using var second = AzureRealtimeFixture.Build(AzureRealtimeFixture.Provider(http, factory, source: source, clock: clock, maximum: kind == "limit" ? 100 : 16 * 1024 * 1024), external, store: store, targets: [target], credential: credentials);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.Realtime.OpenAsync("public", AzureRealtimeFixture.Call(), TestContext.Current.CancellationToken)); Assert.Single(factory.Calls); Assert.Equal(external ? 1 : 0, source.Calls); Assert.Equal(0, http.Creates);
    }
    /// <summary>活跃连接的原缓存绑定丢失后，收发不创建新绑定或切换目标。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MissingActiveBindingFailsBeforeNativeIo(bool send)
    {
        using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory(); var store = AzureFixture.WebSocketStore(); await using var client = AzureRealtimeFixture.Build(AzureRealtimeFixture.Provider(http, factory), store: store); await using var connection = await client.LLM.Realtime.OpenAsync("public", AzureRealtimeFixture.Call(), TestContext.Current.CancellationToken); store.ReadBinding = _ => null;
        if (send) await Assert.ThrowsAsync<InvalidOperationException>(async () => await connection.SendAsync(new() { ["type"] = "response.create" }, TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => AzureRealtimeFixture.Drain(connection, TestContext.Current.CancellationToken));
        Assert.Equal(1, store.GetOrCreateCalls); Assert.Single(factory.Calls); Assert.Empty(Assert.Single(factory.Sockets).Sent);
    }
    /// <summary>未配置原生协议时不试探；一次握手失败或无效 socket 不触发备用目标或 HTTP。</summary>
    [Theory] [InlineData("unsupported")] [InlineData("failed")] [InlineData("closed")] [InlineData("null")]
    public async Task NoImplicitHandshakeProbeOrRetry(string kind)
    {
        using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory(); var socket = new AzureRealtimeSocket(); var target = AzureRealtimeFixture.Target();
        if (kind == "unsupported") target = target with { Protocols = LlmProtocol.ChatCompletions };
        factory.Connect = _ => kind switch { "failed" => throw new WebSocketException("fixture handshake failure"), "null" => Task.FromResult<WebSocket>(null!), _ => Closed() };
        Task<WebSocket> Closed() { socket.Abort(); return Task.FromResult<WebSocket>(socket); }
        await using var client = AzureRealtimeFixture.Build(AzureRealtimeFixture.Provider(http, factory), targets: [target, target with { Id = "other" }]);
        var error = await Record.ExceptionAsync(() => client.LLM.Realtime.OpenAsync("public", AzureRealtimeFixture.Call(), TestContext.Current.CancellationToken)); Assert.NotNull(error);
        if (kind == "unsupported") Assert.IsType<NotSupportedException>(error); else if (kind == "failed") Assert.IsType<WebSocketException>(error); else if (kind == "closed") Assert.IsType<ArgumentException>(error); else Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(kind == "unsupported" ? 0 : 1, factory.Calls.Count); Assert.Equal(kind == "closed" ? 1 : 0, socket.Disposals); if (kind != "closed") socket.Dispose(); Assert.Equal(0, http.Creates);
    }
    /// <summary>端点始终保留代理/端口，部署和版本各占一个编码后的查询值。</summary>
    [Theory] [InlineData(0, "http")] [InlineData(0, "https")] [InlineData(1, "http")] [InlineData(1, "https")]
    public async Task ExplicitEndpointEscapesEachQueryValue(int profile, string scheme)
    {
        using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory(); var options = AzureRealtimeFixture.Options(profile) with { ApiVersion = "v+&?=" }; var provider = new AzureLlmProvider(http, options, null, new AzureRealtimeOptions(), factory);
        var target = AzureRealtimeFixture.Target(profile) with { BaseAddress = new(scheme + "://azure-rt.example.invalid:8123/proxy/openai"), UpstreamModel = "deploy.1/蓝+&" };
        await using var connection = await provider.OpenRealtimeAsync(new(target, AzureFixture.Credential(false), "public", "rt-session"), TestContext.Current.CancellationToken);
        Assert.Equal((scheme == "https" ? "wss" : "ws") + "://azure-rt.example.invalid:8123/proxy/openai/realtime?" + (profile == 0 ? "model=deploy.1%2F%E8%93%9D%2B%26&api-version=v%2B%26%3F%3D" : "api-version=v%2B%26%3F%3D&deployment=deploy.1%2F%E8%93%9D%2B%26"), Assert.Single(factory.Calls).Address.AbsoluteUri);
    }
    /// <summary>非法根地址和原始模型 Unicode 在认证或握手前拒绝。</summary>
    [Theory] [InlineData("relative")] [InlineData("ftp://invalid/root")] [InlineData("https://name:secret@invalid/root")]
    [InlineData("https://invalid/root?x=y")] [InlineData("https://invalid/root#frag")] [InlineData("model-unicode")]
    public async Task InvalidTargetNeverAcquiresTokenOrSocket(string value)
    {
        var clock = new AzureClock(); using var source = new AzureTokenSource(clock); using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory(); var target = AzureRealtimeFixture.Target(); target = value == "model-unicode" ? target with { UpstreamModel = "\ud800" } : target with { BaseAddress = new(value, UriKind.RelativeOrAbsolute) };
        var provider = AzureRealtimeFixture.Provider(http, factory, source: source, clock: clock); var error = await Record.ExceptionAsync(() => provider.OpenRealtimeAsync(new(target, AzureFixture.Credential(true), "public", "rt-session"), TestContext.Current.CancellationToken));
        if (value == "model-unicode") Assert.IsType<EncoderFallbackException>(error); else Assert.IsType<ArgumentException>(error); Assert.Equal(0, source.Calls); Assert.Empty(factory.Calls); Assert.Equal(0, http.Creates);
    }
}
