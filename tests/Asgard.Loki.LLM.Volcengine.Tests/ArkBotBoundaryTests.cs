using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Volcengine.Tests.ArkFixture;
using static Asgard.Loki.LLM.Volcengine.Tests.ArkBotFixture;

namespace Asgard.Loki.LLM.Volcengine.Tests;

/// <summary>固定应用调用的原生输入、身份和终态不得因非法参数或晚到资源故障被修补。</summary>
public sealed class ArkBotBoundaryTests
{
    /// <summary>调用前拒绝所有者字段/别名、非原生消息、非法原始 JSON/Unicode 和请求预算。</summary>
    [Theory] [InlineData("model")] [InlineData("Model")] [InlineData("stream")] [InlineData("Stream")] [InlineData("messages")] [InlineData("unicode")] [InlineData("duplicate")] [InlineData("budget")] [InlineData("cancel")]
    public async Task InvalidBotRequest_FailsBeforeFactory(string fault)
    {
        var payload = BotPayload(); var options = BotOptions();
        if (fault is "model" or "Model" or "stream" or "Stream") payload[fault] = false; if (fault == "messages") payload["messages"] = "wrong"; if (fault == "unicode") payload["future"] = new string((char)0xd800, 1); if (fault == "duplicate") payload["future"] = JsonNode.Parse("{\"x\":1,\"x\":2}"); if (fault == "budget") options = options with { HttpOptions = new() { MaximumRequestBytes = 1 } };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); using var factory = new ArkHttpFactory(handler);
        await Assert.ThrowsAnyAsync<Exception>(() => new VolcengineBotClient(factory, options, Key).CompleteAsync(payload, fault == "cancel" ? new CancellationToken(true) : TestContext.Current.CancellationToken)); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>非法固定配置和原始凭据不触发工厂或远端配置发现。</summary>
    [Theory] [InlineData("root")] [InlineData("userinfo")] [InlineData("bot")] [InlineData("key")] [InlineData("policy")]
    public void InvalidBotConfiguration_DoesNotCreateClient(string fault)
    {
        var options = BotOptions(); options = fault switch { "root" => options with { BaseAddress = new("https://native.invalid/bots?x=1") }, "userinfo" => options with { BaseAddress = new("https://user:pass@native.invalid/bots/") }, "bot" => options with { BotId = new string((char)0xd800, 1) }, "policy" => options with { HttpOptions = new() { MaximumBodyBytes = 0 } }, _ => options };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); using var factory = new ArkHttpFactory(handler); Assert.ThrowsAny<Exception>(() => new VolcengineBotClient(factory, options, fault == "key" ? "invalid key" : Key)); Assert.Empty(factory.Clients);
    }
    /// <summary>正常 EOF、声明长度、终态尾部、异步/同步资源和最终取消全部先于 Bot 全局终态交付。</summary>
    [Theory] [InlineData("read")] [InlineData("dispose")] [InlineData("async")] [InlineData("cancel-eof")] [InlineData("cancel-client")] [InlineData("client")] [InlineData("uri")] [InlineData("length")] [InlineData("tail")] [InlineData("bytes")] [InlineData("no-done")] [InlineData("late-error")]
    public async Task BotTerminal_WaitsForEveryTransportBoundary(string fault)
    {
        using var cancellation = new CancellationTokenSource(); var wire = Text("bot-events.sse"); if (fault == "no-done") wire = Wire(BotFrames(), false);
        const string error = "{\"error\":{\"code\":\"LateNativeError\",\"future\":null}}"; if (fault == "late-error") wire += "data: " + error + Environment.NewLine + Environment.NewLine; var bytes = Bytes(wire);
        var source = new OllamaBodyStream(bytes) { FailAtEof = fault == "read", FailDispose = fault == "dispose", OnEof = fault == "cancel-eof" ? cancellation.Cancel : null }; Stream body = fault == "async" ? new ArkAsyncDisposeStream(bytes) : source;
        HttpRequestMessage? actual = null; using var handler = new StubHttpHandler((message, _) => { actual = message; return Task.FromResult(Reply(body, true, fault == "length" ? bytes.Length + 1 : null)); });
        using var factory = new ArkHttpFactory(handler) { OnDispose = () => { if (fault == "cancel-client") cancellation.Cancel(); if (fault == "client") throw new IOException("close"); if (fault == "uri") actual!.RequestUri = new("https://other.invalid/bots/"); } };
        var options = BotOptions(); if (fault == "tail") options = options with { HttpOptions = new() { MaximumBufferedTerminalEvents = 1 } }; if (fault == "bytes") options = options with { HttpOptions = new() { MaximumBodyBytes = bytes.Length - 1 } };
        List<VolcengineBotEvent> delivered = []; var exception = await Assert.ThrowsAnyAsync<Exception>(() => BotStream(new(factory, options, Key), cancellation.Token, delivered.Add)); if (fault == "late-error") Assert.Equal(error, Assert.IsType<LlmUpstreamException>(exception).ResponseBody);
        Assert.DoesNotContain(delivered, x => x.IsTerminal || x.Json.HasValue && Ending(x.Json.Value)); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>JSON 也必须等所有拥有资源关闭，不能只在 SSE 方法上落实最终边界。</summary>
    [Theory] [InlineData("dispose")] [InlineData("async")] [InlineData("cancel-client")] [InlineData("uri")]
    public async Task CompleteBotResult_WaitsForResourceClosure(string fault)
    {
        using var cancellation = new CancellationTokenSource(); var bytes = Bytes(BotPart("result").GetRawText()); Stream body = fault == "async" ? new ArkAsyncDisposeStream(bytes) : new OllamaBodyStream(bytes) { FailDispose = fault == "dispose" };
        HttpRequestMessage? actual = null; using var handler = new StubHttpHandler((message, _) => { actual = message; return Task.FromResult(Reply(body, false)); }); using var factory = new ArkHttpFactory(handler) { OnDispose = () => { if (fault == "cancel-client") cancellation.Cancel(); if (fault == "uri") actual!.RequestUri = new("https://other.invalid/bots/"); } };
        await Assert.ThrowsAnyAsync<Exception>(() => new VolcengineBotClient(factory, BotOptions(), Key).CompleteAsync(BotPayload(), cancellation.Token)); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>原生早期错误保留为终态事件；JSON/HTTP 错误保留字节和状态，不变成插件成功或重试。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeBotError_IsPreservedWithoutReplay(bool stream)
    {
        var error = Part("error").GetRawText(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(stream ? Wire([error], false) : error)), stream))); using var factory = new ArkHttpFactory(handler); var client = new VolcengineBotClient(factory, BotOptions(), Key);
        if (stream) { var item = Assert.Single(await BotStream(client, TestContext.Current.CancellationToken)); Assert.Equal(error, item.Data); Assert.True(item.IsTerminal); }
        else { var exception = await Assert.ThrowsAsync<LlmUpstreamException>(() => client.CompleteAsync(BotPayload(), TestContext.Current.CancellationToken)); Assert.Equal(error, exception.ResponseBody); }
        Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
}
