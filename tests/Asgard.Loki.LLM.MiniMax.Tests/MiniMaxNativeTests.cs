using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.MiniMax.Tests.MiniMaxFixture;

namespace Asgard.Loki.LLM.MiniMax.Tests;

/// <summary>原生 v2 完整请求/响应/SSE、错误及会话身份；不把字段发送当成模型效果证明。</summary>
public sealed class MiniMaxNativeTests
{
    /// <summary>原生平台和明确代理根均逐字段核对原始 SDK 调用。</summary>
    [Theory] [InlineData(0, false)] [InlineData(0, true)] [InlineData(1, false)] [InlineData(1, true)] [InlineData(2, false)] [InlineData(2, true)]
    public async Task WholeWindows_ThroughPublicSdk(int profile, bool stream)
    {
        var source = stream ? Golden.GetProperty("sse").GetString()! : Golden.GetProperty("result").GetRawText(); var bytes = Bytes(source); var body = new OllamaBodyStream(bytes);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Golden.GetProperty("profiles")[profile].GetProperty("root").GetString()+Golden.GetProperty("path").GetString(), message.RequestUri!.OriginalString); Assert.Equal(message.RequestUri.Authority, message.Headers.Host); Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(Golden.GetProperty("profiles")[profile].GetProperty("key").GetString(), message.Headers.Authorization.Parameter);
            Assert.True(JsonElement.DeepEquals(Golden.GetProperty(stream ? "wire_stream" : "wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, bytes.Length);
        }); var factory = new MiniMaxHttpFactory(handler); var provider = new MiniMaxLlmProvider(factory); var target = Target(provider, profile); if (profile == 2) target = target with { BaseAddress = new(target.BaseAddress.AbsoluteUri.TrimEnd('/')) };
        await using (var sdk = Build(provider, profile, target: target))
        {
            var request = Golden.GetProperty(stream ? "caller_stream" : "caller").Deserialize<ChatCompletionRequest>()!; var before = JsonSerializer.Serialize(request);
            if (stream)
            {
                List<ChatCompletionEvent> seen = []; await foreach (var item in sdk.LLM.ChatCompletions.StreamAsync(request, new() { SessionId = "native" }, TestContext.Current.CancellationToken)) { if (Ending(item)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } seen.Add(item); }
                var expected = Golden.GetProperty("events"); Assert.Equal(expected.GetArrayLength(), seen.Count); for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(expected[i], seen[i].Data)); Assert.Equal("mini-transport", seen[i].EventId); Assert.Equal(0L, seen[i].RetryMilliseconds); Assert.Null(seen[i].EventName); Assert.Equal(target.UpstreamModel, seen[i].Metadata.UpstreamModel); Assert.Null(seen[i].Metadata.Conversion); }
            }
            else { var result = await sdk.LLM.ChatCompletions.CompleteAsync(request, new() { SessionId = "native" }, TestContext.Current.CancellationToken); Assert.True(JsonElement.DeepEquals(Golden.GetProperty("result"), result.Data)); Assert.Equal("reported-native-model", result.Data.GetProperty("model").GetString()); Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); }
            Assert.Equal(before, JsonSerializer.Serialize(request));
        }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原始 DOM 在工厂回调修改之前已经冻结，不依赖事后修复字段。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Snapshot_PrecedesFactory(bool stream)
    {
        var payload = Payload(stream); using var handler = new StubHttpHandler(async (request, token) => { Assert.True(JsonElement.DeepEquals(Golden.GetProperty(stream ? "wire_stream" : "wire"), JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsByteArrayAsync(token)))); return Reply(new MemoryStream(Bytes(stream ? Golden.GetProperty("sse").GetString()! : Golden.GetProperty("result").GetRawText())), stream); }); var factory = new MiniMaxHttpFactory(handler) { Configure = _ => payload["future"] = "caller mutation" }; var provider = new MiniMaxLlmProvider(factory);
        if (stream) await Drain(provider, TestContext.Current.CancellationToken, [], payload); else await provider.CompleteAsync(payload, Context(provider), TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>HTTP 和原生 HTTP200 错误均保留实际字节/状态/媒体，不把 base_resp 当成功。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task OriginalErrors_ArePreserved(bool stream, bool native)
    {
        var error = Golden.GetProperty("error"); var raw = native ? Bytes(JsonSerializer.Serialize(error)) : new byte[] { 0xff, 0, 0xfe }; var status = native ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests;
        var source = native && stream ? Bytes(Frame(error)) : raw; using var handler = new StubHttpHandler((_, _) => { var reply = Reply(new MemoryStream(source), stream && native, status: status); if (!native) reply.Content.Headers.ContentType = new("application/octet-stream"); return Task.FromResult(reply); }); var factory = new MiniMaxHttpFactory(handler); var provider = new MiniMaxLlmProvider(factory);
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { if (stream) await Drain(provider, TestContext.Current.CancellationToken, []); else await provider.CompleteAsync(Payload(), Context(provider), TestContext.Current.CancellationToken); }); Assert.Equal(status, failure.StatusCode); Assert.Equal(raw, failure.Body.ToArray()); Assert.Equal(native ? stream ? "text/event-stream" : "application/json" : "application/octet-stream", failure.ContentType); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>所有成功尾帧在后续原生错误到来时撤回，不泄露已暂存的结束。</summary>
    [Fact]
    public async Task LateNativeError_DiscardsSuccessTail()
    {
        var error = Golden.GetProperty("error"); var source = Golden.GetProperty("sse").GetString()!+Frame(error); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(source)), true))); var factory = new MiniMaxHttpFactory(handler); List<ChatCompletionEvent> seen = [];
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Drain(new(factory), TestContext.Current.CancellationToken, seen)); Assert.Equal(Bytes(JsonSerializer.Serialize(error)), failure.Body.ToArray()); Assert.DoesNotContain(seen, Ending); Assert.Equal(1, handler.Calls);
    }
    /// <summary>完整根、实际模型、凭据及实现设置保持一次绑定，不能在相同公开模型下迁移。</summary>
    [Theory] [InlineData("root")] [InlineData("key")] [InlineData("model")] [InlineData("client")] [InlineData("policy")] [InlineData("version")]
    public async Task SessionDrift_FailsBeforeAnotherRequest(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(Golden.GetProperty("result").GetRawText())), false))); var factory = new MiniMaxHttpFactory(handler); var store = Store(); var provider = new MiniMaxLlmProvider(factory);
        await using (var first = Build(provider, store: store)) await first.LLM.ChatCompletions.CompleteAsync(Golden.GetProperty("caller").Deserialize<ChatCompletionRequest>()!, new() { SessionId = "affinity" }, TestContext.Current.CancellationToken);
        provider = new(factory, change == "policy" ? new() { MaximumRequestBytes = 4096 } : null, httpClientName: change == "client" ? "other" : "Asgard.Loki.LLM.MiniMax"); var target = Target(provider);
        target = change switch { "root" => target with { BaseAddress = Target(provider, 2).BaseAddress }, "model" => target with { UpstreamModel = "other" }, "version" => target with { Version = "2" }, _ => target };
        await using var second = Build(provider, store: store, target: target, key: change == "key" ? "another-key" : null); await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.ChatCompletions.CompleteAsync(Golden.GetProperty("caller").Deserialize<ChatCompletionRequest>()!, new() { SessionId = "affinity" }, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>相同配置可跨实例恢复完整会话绑定，仍由调用者明确发送原生历史。</summary>
    [Fact]
    public async Task SameSession_RecreatesWithoutHiddenHistory()
    {
        using var handler = new StubHttpHandler(async (request, token) => { Assert.True(JsonElement.DeepEquals(Golden.GetProperty("wire"), JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsByteArrayAsync(token)))); return Reply(new MemoryStream(Bytes(Golden.GetProperty("result").GetRawText())), false); }); var factory = new MiniMaxHttpFactory(handler); var store = Store();
        for (var i = 0; i < 2; i++) { await using var sdk = Build(new(factory), store: store); await sdk.LLM.ChatCompletions.CompleteAsync(Golden.GetProperty("caller").Deserialize<ChatCompletionRequest>()!, new() { SessionId = "same" }, TestContext.Current.CancellationToken); } Assert.Equal(2, handler.Calls); Assert.False(factory.Disposed);
    }
}
