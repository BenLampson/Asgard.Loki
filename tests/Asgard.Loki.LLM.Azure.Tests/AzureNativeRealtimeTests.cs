using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>完整原生双工窗口和显式 Azure 认证/地址，错误与每个响应状态仍为原生数据。</summary>
public sealed class AzureNativeRealtimeTests
{
    /// <summary>公共 SDK 两种地址/认证的全部消息匹配手写金样，原生错误不会提前停止后续事件。</summary>
    [Theory] [InlineData(0, false)] [InlineData(0, true)] [InlineData(1, false)] [InlineData(1, true)]
    public async Task CompleteTranscriptKeepsEveryNativeEvent(int profile, bool external)
    {
        var token = TestContext.Current.CancellationToken; var clock = new AzureClock(); using var source = new AzureTokenSource(clock); using var http = AzureCompactionFixture.Factory(); http.OnSend = (_, _) => throw new InvalidOperationException("No HTTP fallback"); using var factory = new AzureRealtimeFactory();
        var provider = AzureRealtimeFixture.Provider(http, factory, profile, source, clock); await using var client = AzureRealtimeFixture.Build(provider, external, profile);
        var connection = await client.LLM.Realtime.OpenAsync("public", AzureRealtimeFixture.Call(), token); var socket = Assert.Single(factory.Sockets);
        await using (connection)
        {
            var handshake = Assert.Single(factory.Calls); Assert.Equal(AzureRealtimeFixture.Part("profiles")[profile].GetProperty("endpoint").GetString(), handshake.Address.AbsoluteUri); Assert.Single(handshake.Headers);
            Assert.Equal(external ? "Bearer fixture-token-1" : "fixture-key", handshake.Headers[external ? "Authorization" : "api-key"]);
            foreach (var item in AzureRealtimeFixture.Part("caller_events").EnumerateArray()) { var message = JsonNode.Parse(item.GetRawText())!.AsObject(); var before = message.ToJsonString(); await connection.SendAsync(message, token); Assert.Equal(before, message.ToJsonString()); }
            var actual = socket.Sent.ToArray(); var expected = AzureRealtimeFixture.Part("wire_events"); Assert.Equal(expected.GetArrayLength(), actual.Length);
            for (var index = 0; index < actual.Length; index++) { using var sent = JsonDocument.Parse(actual[index].Bytes); Assert.True(JsonElement.DeepEquals(expected[index], sent.RootElement)); Assert.Equal(WebSocketMessageType.Text, actual[index].Type); Assert.True(actual[index].End); }
            var native = AzureRealtimeFixture.Part("server_events"); foreach (var item in native.EnumerateArray()) socket.Text(item.GetRawText(), 1); socket.PeerClose(); var events = await AzureRealtimeFixture.Drain(connection, token);
            Assert.Equal(native.GetArrayLength(), events.Count); for (var index = 0; index < events.Count; index++) { Assert.True(JsonElement.DeepEquals(native[index], events[index].Data)); Assert.Equal(new LlmResultMetadata("public", "deploy.rt.1", "azure", "primary"), events[index].Metadata); }
            Assert.Equal(new[] { "failed", "cancelled", "completed" }, events.Where(e => e.Type == "response.done").Select(e => e.Data.GetProperty("response").GetProperty("status").GetString()));
            Assert.Equal("call-original", events[4].Data.GetProperty("call_id").GetString()); Assert.Equal("item-original", events[4].Data.GetProperty("item_id").GetString()); Assert.Equal("9007199254740993", events[9].Data.GetProperty("payload").GetProperty("exact").GetRawText()); Assert.Equal(1, socket.CloseOutputs);
        }
        Assert.Equal(1, socket.Disposals); Assert.Equal(external ? 1 : 0, source.Calls); Assert.False(source.Disposed); Assert.Equal(0, factory.Disposals); Assert.Equal(0, http.Creates); Assert.Single(factory.Calls);
    }
    /// <summary>只有增量后正常关闭也只是传输结束，不创造 response.done 或生成成功。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PeerCloseDoesNotSynthesizeCompletion(bool partial)
    {
        using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory(); await using var client = AzureRealtimeFixture.Build(AzureRealtimeFixture.Provider(http, factory)); await using var connection = await client.LLM.Realtime.OpenAsync("public", AzureRealtimeFixture.Call(), TestContext.Current.CancellationToken);
        var socket = Assert.Single(factory.Sockets); if (partial) socket.Text("""{"type":"response.output_audio.delta","response_id":"r","delta":"AA=="}"""); socket.PeerClose(); var events = await AzureRealtimeFixture.Drain(connection, TestContext.Current.CancellationToken);
        Assert.Equal(partial ? 1 : 0, events.Count); Assert.DoesNotContain(events, e => e.Type == "response.done"); Assert.Empty(socket.Sent); Assert.Equal(0, http.Creates);
    }
    /// <summary>两个配置相同的旧 HTTP 构造保持既有绑定；显式 Realtime 配置、上限或工厂身份独立进入指纹。</summary>
    [Fact]
    public void ExplicitRealtimeConfigurationParticipatesInIdentity()
    {
        using var http = AzureCompactionFixture.Factory(); using var first = new AzureRealtimeFactory(); using var second = new AzureRealtimeFactory { Version = "2" };
        var original = new AzureLlmProvider(http); Assert.NotEqual(original.BindingIdentity, AzureRealtimeFixture.Provider(http, first).BindingIdentity);
        Assert.NotEqual(AzureRealtimeFixture.Provider(http, first).BindingIdentity, AzureRealtimeFixture.Provider(http, second).BindingIdentity); Assert.NotEqual(AzureRealtimeFixture.Provider(http, first).BindingIdentity, AzureRealtimeFixture.Provider(http, first, maximum: 100).BindingIdentity); Assert.Equal(0, http.Creates);
    }
    /// <summary>非法消息上限构造失败，不建立连接。</summary>
    [Theory] [InlineData(0)] [InlineData(-1)]
    public void InvalidRealtimePolicyFailsBeforeHandshake(int maximum)
    { using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory(); Assert.Throws<ArgumentOutOfRangeException>(() => AzureRealtimeFixture.Provider(http, factory, maximum: maximum)); Assert.Empty(factory.Calls); }
}
