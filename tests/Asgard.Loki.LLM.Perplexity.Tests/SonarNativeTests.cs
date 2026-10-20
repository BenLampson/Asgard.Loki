using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Perplexity.Tests.SonarFixture;

namespace Asgard.Loki.LLM.Perplexity.Tests;

/// <summary>原生 Sonar 完整窗口、显式路径、参数保真和完整会话绑定。</summary>
public sealed class SonarNativeTests
{
    /// <summary>官方 SDK 路径、显式 v1 路径和代理根均校验实际公共 SDK 调用。</summary>
    [Theory] [InlineData(0, false)] [InlineData(0, true)] [InlineData(1, false)] [InlineData(1, true)] [InlineData(2, false)] [InlineData(2, true)]
    public async Task WholeNativeWindows_ThroughSdk(int profile, bool stream)
    {
        var bytes = Bytes(stream ? Golden.GetProperty("literal_sse").GetString()! : Golden.GetProperty("result").GetRawText()); var body = new OllamaBodyStream(bytes); var selected = Golden.GetProperty("profiles")[profile];
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(selected.GetProperty("root").GetString()+selected.GetProperty("path").GetString(), message.RequestUri!.OriginalString); Assert.Equal(message.RequestUri.Authority, message.Headers.Host); Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(selected.GetProperty("key").GetString(), message.Headers.Authorization.Parameter);
            Assert.Equal(stream ? "text/event-stream" : "application/json", Assert.Single(message.Headers.Accept).MediaType); Assert.True(JsonElement.DeepEquals(Golden.GetProperty(stream ? "wire_stream" : "wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, bytes.Length);
        }); var factory = new PerplexityHttpFactory(handler); var provider = new PerplexitySonarProvider(factory, Policy(profile)); var target = Target(provider, profile); if (profile == 2) target = target with { BaseAddress = new(target.BaseAddress.AbsoluteUri.TrimEnd('/')) };
        await using (var sdk = Build(provider, profile, target: target))
        {
            var request = Golden.GetProperty("caller").Deserialize<ChatCompletionRequest>()!; var before = JsonSerializer.Serialize(request);
            if (stream)
            {
                List<ChatCompletionEvent> seen = []; await foreach (var item in sdk.LLM.ChatCompletions.StreamAsync(request, new() { SessionId = "sonar" }, TestContext.Current.CancellationToken))
                { if (Ending(item)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } seen.Add(item); }
                var expected = Golden.GetProperty("events"); Assert.Equal(expected.GetArrayLength(), seen.Count);
                for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(expected[i], seen[i].Data)); Assert.Equal("sonar-transport", seen[i].EventId); Assert.Null(seen[i].EventName); Assert.Equal(0L, seen[i].RetryMilliseconds); Assert.Equal(target.UpstreamModel, seen[i].Metadata.UpstreamModel); Assert.Null(seen[i].Metadata.Conversion); }
            }
            else { var result = await sdk.LLM.ChatCompletions.CompleteAsync(request, new() { SessionId = "sonar" }, TestContext.Current.CancellationToken); Assert.True(JsonElement.DeepEquals(Golden.GetProperty("result"), result.Data)); Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); }
            Assert.Equal(before, JsonSerializer.Serialize(request));
        }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>DOM 在工厂回调前冻结，不随调用者并发修改工具历史或原生参数。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Snapshot_PrecedesFactory(bool stream)
    {
        var payload = Payload(); using var handler = new StubHttpHandler(async (request, token) => { Assert.True(JsonElement.DeepEquals(Golden.GetProperty(stream ? "wire_stream" : "wire"), JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsByteArrayAsync(token)))); return Reply(new MemoryStream(Bytes(stream ? Golden.GetProperty("literal_sse").GetString()! : Golden.GetProperty("result").GetRawText())), stream); }); var factory = new PerplexityHttpFactory(handler) { Configure = _ => payload["messages"]![4]!["tool_call_id"] = "changed" }; var provider = new PerplexitySonarProvider(factory);
        if (stream) await Drain(provider, TestContext.Current.CancellationToken, [], payload); else await provider.CompleteAsync(payload, Context(provider), TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生 HTTP 和 HTTP200 错误保留正文、状态和媒体，不删除参数或重试。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task OriginalErrors_ArePreserved(bool stream, bool native)
    {
        var error = Golden.GetProperty("error"); var raw = native ? Bytes(JsonSerializer.Serialize(error)) : new byte[] { 0xff, 0, 0xfe }; var status = native ? HttpStatusCode.OK : HttpStatusCode.BadRequest;
        using var handler = new StubHttpHandler((_, _) => { var reply = Reply(new MemoryStream(native && stream ? Bytes(Frame(error)) : raw), native && stream, status: status); if (!native) reply.Content.Headers.ContentType = new("application/octet-stream"); return Task.FromResult(reply); }); var factory = new PerplexityHttpFactory(handler); var provider = new PerplexitySonarProvider(factory);
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { if (stream) await Drain(provider, TestContext.Current.CancellationToken, []); else await provider.CompleteAsync(Payload(), Context(provider), TestContext.Current.CancellationToken); }); Assert.Equal(status, failure.StatusCode); Assert.Equal(raw, failure.Body.ToArray()); Assert.Equal(native ? stream ? "text/event-stream" : "application/json" : "application/octet-stream", failure.ContentType); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>完整目标、显式端点、客户端及资源策略都是原生 Session 身份的一部分。</summary>
    [Theory] [InlineData("root")] [InlineData("key")] [InlineData("model")] [InlineData("client")] [InlineData("policy")] [InlineData("endpoint")] [InlineData("version")]
    public async Task SessionDrift_PrecedesAnotherRequest(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(Golden.GetProperty("result").GetRawText())), false))); var factory = new PerplexityHttpFactory(handler); var store = Store(); var provider = new PerplexitySonarProvider(factory);
        await using (var first = Build(provider, store: store)) await first.LLM.ChatCompletions.CompleteAsync(Golden.GetProperty("caller").Deserialize<ChatCompletionRequest>()!, new() { SessionId = "affinity" }, TestContext.Current.CancellationToken);
        var policy = change switch { "policy" => new PerplexitySonarOptions { MaximumRequestBytes = 4096 }, "endpoint" => new() { Endpoint = PerplexitySonarEndpoint.SonarV1 }, _ => new() }; provider = new(factory, policy, httpClientName: change == "client" ? "other" : "Asgard.Loki.LLM.Perplexity.Sonar"); var target = Target(provider);
        target = change switch { "root" => target with { BaseAddress = new("https://other.invalid/") }, "model" => target with { UpstreamModel = "other" }, "version" => target with { Version = "2" }, _ => target };
        await using var second = Build(provider, store: store, target: target, key: change == "key" ? "another-key" : null); await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.ChatCompletions.CompleteAsync(Golden.GetProperty("caller").Deserialize<ChatCompletionRequest>()!, new() { SessionId = "affinity" }, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>同配置跨实例恢复会话时仍原样发送显式历史，不在 SDK 中运行工具循环。</summary>
    [Fact]
    public async Task SameBinding_Recreates()
    {
        using var handler = new StubHttpHandler(async (message, token) => { Assert.True(JsonElement.DeepEquals(Golden.GetProperty("wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new MemoryStream(Bytes(Golden.GetProperty("result").GetRawText())), false); }); var factory = new PerplexityHttpFactory(handler); var store = Store();
        for (var i = 0; i < 2; i++) { await using var sdk = Build(new(factory), store: store); await sdk.LLM.ChatCompletions.CompleteAsync(Golden.GetProperty("caller").Deserialize<ChatCompletionRequest>()!, new() { SessionId = "same" }, TestContext.Current.CancellationToken); } Assert.Equal(2, handler.Calls); Assert.False(factory.Disposed);
    }
    /// <summary>同步原生 PENDING、无结束原因、空选择及未知结果字段保持原文，不启动轮询。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeJsonStatus_IsData(bool empty)
    {
        var data = JsonNode.Parse(Golden.GetProperty("result").GetRawText())!.AsObject(); data["status"] = "PENDING"; data["model"] = "reported-model"; if (empty) data["choices"] = new JsonArray(); else data["choices"]![0]!.AsObject().Remove("finish_reason");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(data.ToJsonString())), false))); var factory = new PerplexityHttpFactory(handler); var provider = new PerplexitySonarProvider(factory); var result = await provider.CompleteAsync(Payload(), Context(provider), TestContext.Current.CancellationToken); Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(data), result.Data)); Assert.Equal("sonar", result.Metadata.UpstreamModel); Assert.Equal(1, handler.Calls);
    }
}
