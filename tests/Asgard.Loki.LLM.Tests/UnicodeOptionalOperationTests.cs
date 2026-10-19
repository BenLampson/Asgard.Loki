using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Tests;

/// <summary>可选压缩、双工事件和动态适配身份同样不能依赖替代字符序列化。</summary>
public sealed class UnicodeOptionalOperationTests
{
    /// <summary>压缩在快照和 Session 等待之前检查正文、扩展参数和原始响应 ID。</summary>
    [Theory] [InlineData("input")] [InlineData("parameter-key")] [InlineData("previous")]
    public async Task Compaction_InvalidUnicodeDoesNotReachCacheOrProvider(string location)
    {
        var provider = new UnicodeCompactionProvider(); var store = WebSocketFixture.Store(); var reads = 0; store.BeforeRead = _ => { reads++; return Task.CompletedTask; };
        var options = UnicodeSnapshotFixture.Options(provider, store); var route = options.Routes[0]; options = options with { Routes = [route with { Targets = [route.Targets[0] with { Protocols = LlmProtocol.Responses, Features = LlmTargetFeatures.ResponsesCompaction }] }] };
        await using var client = new LokiClientBuilder().AddLLM(options).Build();
        var request = new ResponseCompactionRequest { Model = "public", Input = JsonValue.Create(location == "input" ? "\ud800" : "valid"), PreviousResponseId = location == "previous" ? "\ud800" : null };
        if (location == "parameter-key") request.Parameters["future\ud800"] = JsonSerializer.SerializeToElement(false);
        await Assert.ThrowsAsync<EncoderFallbackException>(() => client.LLM.Responses.CompactAsync(request, new() { SessionId = "compact" }, TestContext.Current.CancellationToken));
        Assert.Equal(0, reads); Assert.Empty(provider.Compacted);
    }
    /// <summary>双工输入在等待缓存或原生发送之前校验；未知控制事件同样保留合法 JSON。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task WebSocket_InvalidUnicodeDoesNotEnterBoundSend(bool key)
    {
        var provider = new StubResponsesWebSocketProvider(); var store = WebSocketFixture.Store(); await using var client = WebSocketFixture.Build(provider, store);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", WebSocketFixture.Call(), TestContext.Current.CancellationToken);
        var native = Assert.Single(provider.Opened).Connection; var reads = 0; store.BeforeRead = _ => { reads++; return Task.CompletedTask; };
        var message = new JsonObject { ["type"] = "vendor.future", [key ? "future\ud800" : "future"] = key ? JsonValue.Create(false) : JsonValue.Create("\ud800") };
        await Assert.ThrowsAsync<EncoderFallbackException>(() => connection.SendAsync(message, TestContext.Current.CancellationToken).AsTask()); Assert.Equal(0, reads); Assert.Empty(native.Sent);
    }
    /// <summary>有效 Unicode 控制字段不归一化，Session 的实际模型绑定照常生效。</summary>
    [Fact]
    public async Task WebSocket_ValidUnicodeIsUnchanged()
    {
        var provider = new StubResponsesWebSocketProvider(); await using var client = WebSocketFixture.Build(provider);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", WebSocketFixture.Call(), TestContext.Current.CancellationToken);
        const string value = "e\u0301🌏\r\n\0"; var message = new JsonObject { ["type"] = "vendor.future", ["future🌏"] = value };
        await connection.SendAsync(message, TestContext.Current.CancellationToken); Assert.Equal(value, Assert.Single(Assert.Single(provider.Opened).Connection.Sent)["future🌏"]!.GetValue<string>());
    }
    /// <summary>实现身份首次和动态变更时均检查，不让不同坏身份编码为同一指纹。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ProviderIdentity_IsStrictBeforeFingerprint(bool changed)
    {
        var provider = new StubResponsesWebSocketProvider();
        if (!changed) { provider.BindingVersion = "\ud800"; Assert.Throws<EncoderFallbackException>(() => WebSocketFixture.Build(provider)); return; }
        await using var client = WebSocketFixture.Build(provider); provider.BindingVersion = "\ud800";
        await Assert.ThrowsAsync<EncoderFallbackException>(() => client.LLM.Responses.OpenWebSocketAsync("public", WebSocketFixture.Call(), TestContext.Current.CancellationToken)); Assert.Empty(provider.Opened);
    }
}
