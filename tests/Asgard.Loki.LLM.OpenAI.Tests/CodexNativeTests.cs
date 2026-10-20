using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.CodexFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>同包原生 Codex 调用，完整字段和并行工具关联不经过 Chat 转换或参数清理。</summary>
public sealed class CodexNativeTests
{
    /// <summary>完整 JSON/compact 请求与独立窗口一致，选中账户/令牌不被工厂默认值替换。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeJson_MatchesFullIndependentWireAndResult(bool compact)
    {
        var body = new OllamaBodyStream(Bytes(Part(compact ? "compaction" : "response").GetRawText())); using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal(Part(compact ? "compact_endpoint" : "responses_endpoint").GetString(), request.RequestUri!.OriginalString);
            Assert.Equal(Token, request.Headers.Authorization!.Parameter); Assert.Equal(Part("account_id").GetString(), Assert.Single(request.Headers.GetValues("chatgpt-account-id")));
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.ToString()); Assert.Equal("loki-fixture", Assert.Single(request.Headers.GetValues("originator")));
            Assert.Equal("responses=experimental", Assert.Single(request.Headers.GetValues("OpenAI-Beta"))); Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
            var wire = JsonSerializer.Deserialize<JsonElement>(await request.Content.ReadAsByteArrayAsync(token)); Assert.True(JsonElement.DeepEquals(Part(compact ? "wire_compact" : "wire_response"), wire));
            return OpenAiHttpFixture.Reply(body, false);
        });
        using var source = new CodexFixtureSource(); var factory = new OpenAiHttpFactory(handler) { Configure = c => { c.DefaultRequestHeaders.Authorization = new("Bearer", "other"); c.DefaultRequestHeaders.Add("chatgpt-account-id", "other"); } }; var provider = Provider(factory, source); var payload = Payload(compact); var before = payload.ToJsonString();
        var result = await Invoke(provider, compact, TestContext.Current.CancellationToken, payload); Assert.Equal(Part(compact ? "compaction" : "response").GetRawText(), result.GetRawText()); Assert.Equal(before, payload.ToJsonString());
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(source.Disposed); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(Token, provider.BindingIdentity); var tokenValue = new CodexAccessToken("principal", "account", Token, Now.AddHours(1)); Assert.DoesNotContain(Token, JsonSerializer.Serialize(tokenValue)); Assert.DoesNotContain(Token, tokenValue.ToString());
    }
    /// <summary>原生可选字段不被加入，甚至服务可能拒绝的调用者设置也不被 SDK 擅自删除。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task OmittedAndExplicitSettings_AreNotSanitized(bool compact)
    {
        var payload = new JsonObject { ["input"] = "原生输入", ["temperature"] = 0, ["max_output_tokens"] = 7, ["future"] = false };
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var actual = JsonNode.Parse(await request.Content!.ReadAsByteArrayAsync(token))!.AsObject(); Assert.False(actual.ContainsKey("instructions")); Assert.False(actual.ContainsKey("store")); Assert.Equal(0, actual["temperature"]!.GetValue<int>()); Assert.Equal(7, actual["max_output_tokens"]!.GetValue<int>()); Assert.Equal(!compact, actual.ContainsKey("stream"));
            return CompactionFixture.Json(Part(compact ? "compaction" : "response").GetRawText());
        }); using var source = new CodexFixtureSource(); var provider = Provider(new OpenAiHttpFactory(handler), source); await Invoke(provider, compact, TestContext.Current.CancellationToken, payload); Assert.Equal(1, handler.Calls);
    }
    /// <summary>所有请求数据在认证源首次等待前冻结，来源/调用者不共享可变报文容器。</summary>
    [Fact]
    public async Task Snapshot_PrecedesTokenAwait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var ready = new TaskCompletionSource<CodexAccessToken>(TaskCreationOptions.RunContinuationsAsynchronously); var payload = Payload();
        using var source = new CodexFixtureSource { Handler = (_, _) => { entered.SetResult(); return new(ready.Task); } };
        using var handler = new StubHttpHandler(async (request, token) => { Assert.True(JsonElement.DeepEquals(Part("wire_response"), JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsByteArrayAsync(token)))); return CompactionFixture.Json(Part("response").GetRawText()); });
        var provider = Provider(new OpenAiHttpFactory(handler), source); var operation = Invoke(provider, false, TestContext.Current.CancellationToken, payload); await entered.Task.WaitAsync(TestContext.Current.CancellationToken); payload.Clear();
        ready.SetResult(new(Part("principal").GetString()!, Part("account_id").GetString()!, Token, Now.AddHours(1))); await operation; Assert.Empty(payload); Assert.Equal(1, source.Calls);
    }
    /// <summary>完整原生 SSE 逐帧保持工具、推理/加密、索引、事件和传输身份；终态在完整资源关闭后可见。</summary>
    [Fact]
    public async Task NativeSse_MatchesAllIndependentFramesWithoutAggregation()
    {
        var body = new OllamaBodyStream(Bytes(Text("events.sse"))); using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.ToString()); Assert.Equal("text/event-stream", Assert.Single(request.Headers.Accept).MediaType);
            var wire = JsonNode.Parse(Part("wire_response").GetRawText())!.AsObject(); wire["stream"] = true; Assert.True(JsonNode.DeepEquals(wire, JsonNode.Parse(await request.Content.ReadAsByteArrayAsync(token)))); return OpenAiHttpFixture.Reply(body, true);
        }); using var source = new CodexFixtureSource(); var factory = new OpenAiHttpFactory(handler); var provider = Provider(factory, source);
        var events = await Stream(provider, TestContext.Current.CancellationToken, item => { if (item.Type == "response.completed") { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } });
        using var golden = JsonDocument.Parse(Text("events.json")); Assert.Equal(golden.RootElement.GetArrayLength(), events.Count);
        for (var i = 0; i < events.Count; i++) { Assert.Equal(golden.RootElement[i].GetRawText(), events[i].Data.GetRawText()); Assert.Equal(golden.RootElement[i].GetProperty("type").GetString(), events[i].EventName); Assert.Equal("transport-codex-a", events[i].EventId); Assert.Equal(0L, events[i].RetryMilliseconds); Assert.Equal("actual-model", events[i].Metadata.UpstreamModel); }
        Assert.Equal(2, events[2].Data.GetProperty("output_index").GetInt32()); Assert.Equal("call-b", events[2].Data.GetProperty("item").GetProperty("call_id").GetString()); Assert.Equal("fc-a", events[4].Data.GetProperty("item_id").GetString()); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.False(source.Disposed);
    }
    /// <summary>HTTP/HTTP200 原生错误和二进制错误保持原始状态/媒体/字节，只取一次令牌和调用一次上游。</summary>
    [Theory] [InlineData(400, false)] [InlineData(200, false)] [InlineData(503, true)]
    public async Task NativeErrors_RetainOriginalBytesAndDoNotRetry(int status, bool binary)
    {
        var bytes = binary ? new byte[] { 0xff, 0x80, 0 } : Bytes(Part("http_error").GetRawText()); var body = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => { var response = OpenAiHttpFixture.Reply(body, false, status: (HttpStatusCode)status); response.Content.Headers.ContentType = new(binary ? "application/octet-stream" : "application/problem+json"); return Task.FromResult(response); });
        using var source = new CodexFixtureSource(); var factory = new OpenAiHttpFactory(handler); var exception = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(Provider(factory, source), false, TestContext.Current.CancellationToken));
        Assert.Equal(bytes, exception.Body.ToArray()); Assert.Equal((HttpStatusCode)status, exception.StatusCode); Assert.Equal(binary ? "application/octet-stream" : "application/problem+json", exception.ContentType); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    /// <summary>JSON/compact 的最终资源关闭也须先于成功交付，不能仅验证 SSE。</summary>
    [Theory] [InlineData(false, "source-body")] [InlineData(true, "source-body")] [InlineData(false, "cancel-client")] [InlineData(true, "cancel-client")] [InlineData(false, "target-client")] [InlineData(true, "target-client")]
    public async Task JsonResourceClosure_PrecedesSuccessfulResult(bool compact, string fault)
    {
        using var cancellation = new CancellationTokenSource(); using var source = new CodexFixtureSource();
        var body = new OllamaBodyStream(Bytes(Part(compact ? "compaction" : "response").GetRawText())) { OnDispose = () => { if (fault == "source-body") source.BindingIdentity = "changed"; } };
        HttpRequestMessage? actual = null; using var handler = new StubHttpHandler((message, _) => { actual = message; return Task.FromResult(OpenAiHttpFixture.Reply(body, false)); });
        var factory = new OpenAiHttpFactory(handler) { OnDispose = () => { if (fault == "cancel-client") cancellation.Cancel(); if (fault == "target-client") actual!.RequestUri = new("https://other.invalid/responses"); } };
        await Assert.ThrowsAnyAsync<Exception>(() => Invoke(Provider(factory, source), compact, cancellation.Token)); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }

}
