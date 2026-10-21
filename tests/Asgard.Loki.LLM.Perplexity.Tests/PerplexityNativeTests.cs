using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Perplexity.Tests.PerplexityFixture;

namespace Asgard.Loki.LLM.Perplexity.Tests;

/// <summary>通过公共 SDK 核对独立完整样本、稀疏失败和完整目标绑定。</summary>
public sealed class PerplexityNativeTests
{
    /// <summary>完整 JSON/SSE 保留工具关联、未知精确数值、原生格式以及传输元数据。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task WholeWindow_ThroughPublicSdk(bool stream)
    {
        var bytes = Bytes(stream ? Golden.GetProperty("literal_sse").GetString()! : Golden.GetProperty("complete").GetRawText()); var body = new OllamaBodyStream(bytes);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Golden.GetProperty("endpoint").GetString(), message.RequestUri!.OriginalString); Assert.Equal(message.RequestUri.Authority, message.Headers.Host); Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(Golden.GetProperty("key").GetString(), message.Headers.Authorization.Parameter);
            Assert.True(JsonElement.DeepEquals(Golden.GetProperty(stream ? "wire_stream" : "wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, bytes.Length);
        }); var factory = new PerplexityHttpFactory(handler); var provider = new PerplexityResponsesProvider(factory);
        await using (var sdk = Build(provider))
        {
            var request = Golden.GetProperty("caller").Deserialize<ResponseRequest>()!; var before = JsonSerializer.Serialize(request);
            if (stream)
            {
                List<ResponseEvent> seen = []; await foreach (var item in sdk.LLM.Responses.StreamAsync(request, new() { SessionId = "native" }, TestContext.Current.CancellationToken))
                { if (Ending(item)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } seen.Add(item); }
                var expected = Golden.GetProperty("events"); Assert.Equal(expected.GetArrayLength(), seen.Count);
                for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(expected[i], seen[i].Data)); Assert.Equal(expected[i].GetProperty("type").GetString(), seen[i].EventName); Assert.Equal("transport-"+i, seen[i].EventId); Assert.Equal(250L, seen[i].RetryMilliseconds); Assert.Equal(Target(provider).UpstreamModel, seen[i].Metadata.UpstreamModel); Assert.Null(seen[i].Metadata.Conversion); }
            }
            else { var result = await sdk.LLM.Responses.CreateAsync(request, new() { SessionId = "native" }, TestContext.Current.CancellationToken); Assert.True(JsonElement.DeepEquals(Golden.GetProperty("complete"), result.Data)); }
            Assert.Equal(before, JsonSerializer.Serialize(request));
        }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>失败可以没有 response/id；在 EOF 和关闭之前不交付失败终态。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SparseFailed_IsOriginalTerminal(bool prefix)
    {
        var failure = JsonNode.Parse(Golden.GetProperty("sparse_failed").GetRawText())!.AsObject(); if (prefix) failure["sequence_number"] = 1;
        var source = (prefix ? Frame(Golden.GetProperty("events")[0]) : "")+"event: response.failed\nid: failed-transport\n"+Frame(JsonSerializer.SerializeToElement(failure));
        var body = new OllamaBodyStream(Bytes(source)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new PerplexityHttpFactory(handler); var provider = new PerplexityResponsesProvider(factory); List<ResponseEvent> seen = [];
        await using var sdk = Build(provider);
        await foreach (var item in sdk.LLM.Responses.StreamAsync(Golden.GetProperty("caller").Deserialize<ResponseRequest>()!, new() { SessionId = "sparse" }, TestContext.Current.CancellationToken)) { if (Ending(item)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } seen.Add(item); }
        var last = seen[^1]; Assert.Equal("response.failed", last.Type); Assert.Null(last.Id); Assert.False(last.Data.TryGetProperty("response", out _)); Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(failure), last.Data)); Assert.Equal("failed-transport", last.EventId); Assert.Equal(prefix ? 2 : 1, seen.Count);
    }
    /// <summary>部分 completed 不补造 ID、output 或状态；原生终态状态与事件名分开保留。</summary>
    [Theory] [InlineData("completed")] [InlineData("incomplete")] [InlineData("failed")] [InlineData("cancelled")] [InlineData(null)]
    public async Task PartialCompleted_PreservesStatus(string? status)
    {
        var data = JsonNode.Parse(Golden.GetProperty("partial_completed").GetRawText())!.AsObject(); if (status is null) data.Remove("response"); else data["response"]!["status"] = status;
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(Frame(JsonSerializer.SerializeToElement(data)))), true))); var factory = new PerplexityHttpFactory(handler); List<ResponseEvent> seen = [];
        await Drain(new(factory), TestContext.Current.CancellationToken, seen); Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(data), Assert.Single(seen).Data)); Assert.Null(seen[0].Id);
    }
    /// <summary>完整响应中的原生状态属于结果，未被重写为成功或传输异常。</summary>
    [Theory] [InlineData("failed")] [InlineData("incomplete")] [InlineData("queued")] [InlineData("in_progress")] [InlineData("cancelled")]
    public async Task NativeJsonStatus_RemainsData(string status)
    {
        var data = JsonNode.Parse(Golden.GetProperty("complete").GetRawText())!.AsObject(); data["status"] = status; data["model"] = "reported-model"; if (status == "failed") data["error"] = new JsonObject { ["message"] = "native failure" };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(data.ToJsonString())), false))); var factory = new PerplexityHttpFactory(handler); var provider = new PerplexityResponsesProvider(factory); var result = await provider.CreateAsync(Payload(), Context(provider), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(data), result.Data)); Assert.Equal(Target(provider).UpstreamModel, result.Metadata.UpstreamModel);
    }
    /// <summary>HTTP 和连接级错误保留实际字节、状态和媒体；不混同原生失败事件。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task TransportErrors_KeepOriginalBytes(bool stream, bool native)
    {
        var error = JsonSerializer.Deserialize<JsonElement>("{\"error\":{\"message\":\"wire failure\"}}"); var raw = native ? Bytes(JsonSerializer.Serialize(error)) : new byte[] { 0xff, 0, 0xfe }; var status = native ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests;
        using var handler = new StubHttpHandler((_, _) => { var reply = Reply(new MemoryStream(native && stream ? Bytes(Frame(error)) : raw), native && stream, status: status); if (!native) reply.Content.Headers.ContentType = new("application/octet-stream"); return Task.FromResult(reply); }); var factory = new PerplexityHttpFactory(handler); var provider = new PerplexityResponsesProvider(factory);
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { if (stream) await Drain(provider, TestContext.Current.CancellationToken, []); else await provider.CreateAsync(Payload(), Context(provider), TestContext.Current.CancellationToken); }); Assert.Equal(status, failure.StatusCode); Assert.Equal(raw, failure.Body.ToArray()); Assert.Equal(native ? stream ? "text/event-stream" : "application/json" : "application/octet-stream", failure.ContentType); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>完整目标、命名客户端和策略都参与 Session 绑定，不能绕过首次选中身份。</summary>
    [Theory] [InlineData("root")] [InlineData("key")] [InlineData("model")] [InlineData("client")] [InlineData("policy")] [InlineData("version")]
    public async Task SessionDrift_PrecedesSecondRequest(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(Golden.GetProperty("complete").GetRawText())), false))); var factory = new PerplexityHttpFactory(handler); var store = Store(); var provider = new PerplexityResponsesProvider(factory);
        await using (var first = Build(provider, store: store)) await first.LLM.Responses.CreateAsync(Golden.GetProperty("caller").Deserialize<ResponseRequest>()!, new() { SessionId = "affinity" }, TestContext.Current.CancellationToken);
        provider = new(factory, change == "policy" ? new() { MaximumRequestBytes = 4096 } : null, httpClientName: change == "client" ? "other" : "Asgard.Loki.LLM.Perplexity"); var target = Target(provider);
        target = change switch { "root" => target with { BaseAddress = new("https://other.invalid/v1/") }, "model" => target with { UpstreamModel = "other" }, "version" => target with { Version = "2" }, _ => target };
        await using var second = Build(provider, store: store, target: target, key: change == "key" ? "another-key" : null); await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.Responses.CreateAsync(Golden.GetProperty("caller").Deserialize<ResponseRequest>()!, new() { SessionId = "affinity" }, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>相同完整配置可跨实例恢复，不添加或保存调用历史。</summary>
    [Fact]
    public async Task SameSession_Recreates()
    {
        using var handler = new StubHttpHandler(async (message, token) => { Assert.True(JsonElement.DeepEquals(Golden.GetProperty("wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new MemoryStream(Bytes(Golden.GetProperty("complete").GetRawText())), false); }); var factory = new PerplexityHttpFactory(handler); var store = Store();
        for (var i = 0; i < 2; i++) { await using var sdk = Build(new(factory), store: store); await sdk.LLM.Responses.CreateAsync(Golden.GetProperty("caller").Deserialize<ResponseRequest>()!, new() { SessionId = "same" }, TestContext.Current.CancellationToken); } Assert.Equal(2, handler.Calls); Assert.False(factory.Disposed);
    }
    /// <summary>原生字符串输入有效；完整请求在工厂回调之前冻结，显式代理根不按主机猜测。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task StringInput_IsFrozenBeforeFactory(bool stream)
    {
        var payload = Payload(); payload["input"] = "原生字符串"; var expected = JsonNode.Parse(Golden.GetProperty(stream ? "wire_stream" : "wire").GetRawText())!.AsObject(); expected["input"] = "原生字符串";
        using var handler = new StubHttpHandler(async (message, token) => { Assert.Equal("https://proxy.invalid/native/v1/responses", message.RequestUri!.OriginalString); Assert.Equal("proxy.invalid", message.Headers.Host); Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new MemoryStream(Bytes(stream ? Golden.GetProperty("literal_sse").GetString()! : Golden.GetProperty("complete").GetRawText())), stream); });
        var factory = new PerplexityHttpFactory(handler) { Configure = _ => payload["input"] = "later mutation" }; var provider = new PerplexityResponsesProvider(factory); var context = Context(provider, Target(provider) with { BaseAddress = new("https://proxy.invalid/native/v1") });
        if (stream) await Drain(provider, TestContext.Current.CancellationToken, [], payload, context); else await provider.CreateAsync(payload, context, TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>store=false 的原生响应仍可在同一绑定下显式续接，不注入隐藏历史。</summary>
    [Fact]
    public async Task ExplicitContinuation_PreservesStoreFalseAndInput()
    {
        var index = 0; using var handler = new StubHttpHandler(async (message, token) => { var expected = JsonNode.Parse(Golden.GetProperty("wire").GetRawText())!.AsObject(); if (index++ == 1) expected["previous_response_id"] = "pplx-response-A"; Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new MemoryStream(Bytes(Golden.GetProperty("complete").GetRawText())), false); });
        var factory = new PerplexityHttpFactory(handler); await using var sdk = Build(new(factory)); var first = Golden.GetProperty("caller").Deserialize<ResponseRequest>()!; var response = await sdk.LLM.Responses.CreateAsync(first, new() { SessionId = "chain" }, TestContext.Current.CancellationToken); var next = first with { PreviousResponseId = response.Id }; await sdk.LLM.Responses.CreateAsync(next, new() { SessionId = "chain" }, TestContext.Current.CancellationToken); Assert.Equal(2, handler.Calls);
    }
}
