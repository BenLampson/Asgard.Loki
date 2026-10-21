using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>Realtime 单次握手和双工 IO 的身份/到期守卫，不刷新、重连或借用其他凭据。</summary>
public sealed class AzureRealtimeAuthenticationTests
{
    /// <summary>所有身份/到期失败在相应边界停止，已取得的 socket 被关闭。</summary>
    [Theory] [InlineData("source-before")] [InlineData("factory-before")] [InlineData("source-acquire")] [InlineData("factory-acquire")]
    [InlineData("principal")] [InlineData("expired")] [InlineData("null")] [InlineData("non-ascii")]
    [InlineData("source-connected")] [InlineData("factory-connected")] [InlineData("expired-connected")]
    public async Task HandshakeAuthenticationMustRemainStable(string kind)
    {
        var clock = new AzureClock(); var initial = clock.Now; using var source = new AzureTokenSource(clock); using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory(); var socket = new AzureRealtimeSocket();
        void Change() { if (kind.StartsWith("source", StringComparison.Ordinal)) source.Version = "changed"; else if (kind.StartsWith("factory", StringComparison.Ordinal)) factory.Version = "changed"; else clock.Now = initial.AddMinutes(1); }
        source.Resolve = (principal, _) =>
        {
            if (kind.EndsWith("acquire", StringComparison.Ordinal)) Change();
            return ValueTask.FromResult(kind == "null" ? null! : new AzureLlmAccessToken(kind == "principal" ? "other-principal" : principal, kind == "non-ascii" ? "凭据" : "fixture-token", kind == "expired" ? initial : initial.AddMinutes(1)));
        };
        factory.Connect = _ => { if (kind.EndsWith("connected", StringComparison.Ordinal)) Change(); return Task.FromResult<WebSocket>(socket); };
        var provider = AzureRealtimeFixture.Provider(http, factory, source: source, clock: clock); if (kind.EndsWith("before", StringComparison.Ordinal)) Change();
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenRealtimeAsync(AzureRealtimeFixture.Context(true), TestContext.Current.CancellationToken));
        var connected = kind.EndsWith("connected", StringComparison.Ordinal); Assert.Equal(connected ? 1 : 0, factory.Calls.Count); Assert.Equal(connected ? 1 : 0, socket.Disposals); Assert.Equal(kind.EndsWith("before", StringComparison.Ordinal) ? 0 : 1, source.Calls); Assert.False(source.Disposed); Assert.Equal(0, factory.Disposals); Assert.Equal(0, http.Creates); if (!connected) socket.Dispose();
    }
    /// <summary>身份变化和到期在实际 IO 前后均拒绝发布结果，并且马上关闭拥有的 socket。</summary>
    [Theory] [InlineData("source", "send-before")] [InlineData("factory", "send-before")] [InlineData("expiry", "send-before")]
    [InlineData("source", "send-after")] [InlineData("factory", "send-after")] [InlineData("expiry", "send-after")]
    [InlineData("source", "read-before")] [InlineData("factory", "read-before")] [InlineData("expiry", "read-before")]
    [InlineData("source", "read-after")] [InlineData("factory", "read-after")] [InlineData("expiry", "read-after")]
    public async Task DuplexAuthenticationGuardClosesWithoutReacquisition(string kind, string boundary)
    {
        var clock = new AzureClock(); using var source = new AzureTokenSource(clock); using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory();
        var provider = AzureRealtimeFixture.Provider(http, factory, source: source, clock: clock); await using var client = AzureRealtimeFixture.Build(provider, true); await using var connection = await client.LLM.Realtime.OpenAsync("public", AzureRealtimeFixture.Call(), TestContext.Current.CancellationToken); var socket = Assert.Single(factory.Sockets);
        void Change() { if (kind == "source") source.Version = "changed"; else if (kind == "factory") factory.Version = "changed"; else clock.Now = clock.Now.AddHours(1); }
        if (boundary.EndsWith("before", StringComparison.Ordinal)) Change(); else if (boundary.StartsWith("send", StringComparison.Ordinal)) socket.AfterSend = Change; else socket.AfterReceive = Change;
        if (boundary.StartsWith("send", StringComparison.Ordinal)) await Assert.ThrowsAsync<InvalidOperationException>(async () => await connection.SendAsync(new() { ["type"] = "future.native" }, TestContext.Current.CancellationToken));
        else { socket.Text("""{"type":"future.native","future":false}"""); await Assert.ThrowsAsync<InvalidOperationException>(() => AzureRealtimeFixture.Drain(connection, TestContext.Current.CancellationToken)); }
        Assert.Equal(boundary == "send-after" ? 1 : 0, socket.SendAttempts); Assert.Equal(1, socket.Disposals); Assert.Equal(1, source.Calls); Assert.Single(factory.Calls); Assert.Equal(0, factory.Disposals); Assert.Equal(0, http.Creates);
    }
    /// <summary>取消发生在握手前、认证中或拿到 socket 后都不返回连接，不能重试认证。</summary>
    [Theory] [InlineData("before")] [InlineData("source")] [InlineData("connected")]
    public async Task HandshakeCancellationDoesNotLeakOrRetry(string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var clock = new AzureClock(); using var source = new AzureTokenSource(clock); using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory(); var socket = new AzureRealtimeSocket();
        source.Resolve = (principal, _) => { if (kind == "source") cancellation.Cancel(); return ValueTask.FromResult(new AzureLlmAccessToken(principal, "fixture-token", clock.Now.AddHours(1))); };
        factory.Connect = _ => { cancellation.Cancel(); return Task.FromResult<WebSocket>(socket); }; if (kind == "before") cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AzureRealtimeFixture.Provider(http, factory, source: source, clock: clock).OpenRealtimeAsync(AzureRealtimeFixture.Context(true), cancellation.Token));
        Assert.Equal(kind == "before" ? 0 : 1, source.Calls); Assert.Equal(kind == "connected" ? 1 : 0, factory.Calls.Count); Assert.Equal(kind == "connected" ? 1 : 0, socket.Disposals); if (kind != "connected") socket.Dispose(); Assert.False(source.Disposed);
    }
    /// <summary>API Key 不调用源，即使源身份已经改变；地址不携带密钥。</summary>
    [Fact]
    public async Task ApiKeyDoesNotAcquireExternalCredentials()
    {
        var clock = new AzureClock(); using var source = new AzureTokenSource(clock); using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory(); var provider = AzureRealtimeFixture.Provider(http, factory, source: source, clock: clock); source.Version = "changed";
        await using var connection = await provider.OpenRealtimeAsync(AzureRealtimeFixture.Context(), TestContext.Current.CancellationToken); var handshake = Assert.Single(factory.Calls); Assert.Equal("fixture-key", Assert.Single(handshake.Headers).Value); Assert.DoesNotContain("fixture-key", handshake.Address.AbsoluteUri); Assert.Equal(0, source.Calls);
    }
}
