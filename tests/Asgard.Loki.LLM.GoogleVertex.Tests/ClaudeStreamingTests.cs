using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>Vertex Claude 原始 SSE 与完整事件金样独立编写，保留关联、签名、增量和累计用量。</summary>
public sealed class ClaudeStreamingTests
{
    /// <summary>原生 streamRawPredict 使用完整资源和正文版本，潜在终态通过 EOF/释放后才可见。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task FullNativeEvents_MatchIndependentGoldens(bool quota, bool version)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(ClaudeFixture.Sse())); using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("https://first.invalid/proxy/v1/" + ClaudeFixture.Resource.Replace("@", "%40") + ":streamRawPredict", request.RequestUri!.AbsoluteUri); Assert.Equal("text/event-stream", Assert.Single(request.Headers.Accept).MediaType); Assert.Equal("Bearer fixture-token-1", request.Headers.Authorization!.ToString());
            var expected = JsonNode.Parse(VertexFixture.Text("claude-wire"))!.AsObject(); expected["stream"] = true; Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); Assert.False(request.Headers.Contains("x-api-key")); Assert.False(request.Headers.Contains("anthropic-version"));
            if (quota) Assert.Equal("quota-project", Assert.Single(request.Headers.GetValues("x-goog-user-project"))); else Assert.False(request.Headers.Contains("x-goog-user-project")); return StreamingTests.Reply(stream);
        }); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexLlmProvider(factory, source, new() { QuotaProjectId = quota ? "quota-project" : null, ClaudeAnthropicVersion = version ? "vertex-2023-10-16" : null }, timeProvider: clock); await using var client = ClaudeFixture.Client(provider); var input = ClaudeFixture.Request(); var original = input.ToJsonString(); var events = JsonSerializer.Deserialize<JsonElement>(VertexFixture.Text("claude-events")); var index = 0;
        await foreach (var item in client.LLM.ClaudeMessages.StreamAsync(input.Deserialize<ClaudeMessageRequest>()!, new() { SessionId = "vertex-claude-session" }, TestContext.Current.CancellationToken))
        {
            Assert.True(JsonElement.DeepEquals(events[index], item.Data)); Assert.Equal(events[index].GetProperty("type").GetString(), item.EventName); Assert.Equal("wire-claude", item.EventId); Assert.Equal(1200L, item.RetryMilliseconds); Assert.Equal(ClaudeFixture.Resource, item.Metadata.UpstreamModel);
            Assert.Equal(index >= 11, stream.Eof); Assert.Equal(index >= 11, stream.Disposed); Assert.Equal(index >= 11, Assert.Single(factory.Clients).Disposed); index++;
        }
        Assert.Equal(13, index); Assert.Equal(original, input.ToJsonString()); Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>模型原生不同结束原因均保留，不把截断、拒绝或暂停改为 end_turn。</summary>
    [Theory] [InlineData("max_tokens")] [InlineData("refusal")] [InlineData("pause_turn")] [InlineData("future_reason")]
    public async Task OriginalStopReason_IsNotRewritten(string reason)
    {
        var data = "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"" + reason + "\"},\"usage\":{\"output_tokens\":7}}"; var text = Start + "data: " + data + "\n\ndata: {\"type\":\"message_stop\"}\n\n"; var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(text)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var observed = new List<ClaudeMessageEvent>();
        await foreach (var item in Enumerate(new(factory, new VertexTokenSource(clock), timeProvider: clock), TestContext.Current.CancellationToken)) observed.Add(item);
        Assert.Equal(data, observed[1].Data.GetRawText()); Assert.Equal("message_stop", observed[^1].Type); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>原生 error 可在开始前或部分生成后发生，不补造 message_stop；失败终态同样等待 EOF。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeError_RemainsFailureEventAfterNormalEof(bool started)
    {
        const string error = "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"原始错误\",\"future\":false}}"; var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes((started ? Start : "") + "event: error\ndata: " + error + "\n\n")); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var events = new List<ClaudeMessageEvent>();
        await foreach (var item in Enumerate(new(factory, new VertexTokenSource(clock), timeProvider: clock), TestContext.Current.CancellationToken)) { if (item.Type == "error") { Assert.True(stream.Eof); Assert.True(stream.Disposed); } events.Add(item); }
        Assert.Equal(error, events[^1].Data.GetRawText()); Assert.DoesNotContain(events, item => item.Type == "message_stop"); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>累计用量更新和 ping/未来元数据原序保留，不把同值 stop_reason 当重复 message_stop。</summary>
    [Fact]
    public async Task CumulativeUsageAndFutureTail_RemainNative()
    {
        const string reason = "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":6}}\n\n";
        var text = Start + reason + reason.Replace(":6", ":7") + "data: {\"type\":\"message_stop\"}\n\ndata: {\"type\":\"ping\"}\n\ndata: {\"type\":\"vertex.claude.future\",\"future\":9007199254740993}\n\n"; var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(text)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var events = new List<ClaudeMessageEvent>();
        await foreach (var item in Enumerate(new(factory, new VertexTokenSource(clock), timeProvider: clock), TestContext.Current.CancellationToken)) { if (events.Count > 0) Assert.True(stream.Disposed); events.Add(item); }
        Assert.Equal(6, events.Count); Assert.Equal(6, events[1].Data.GetProperty("usage").GetProperty("output_tokens").GetInt32()); Assert.Equal(7, events[2].Data.GetProperty("usage").GetProperty("output_tokens").GetInt32()); Assert.Equal(9007199254740993L, events[^1].Data.GetProperty("future").GetInt64());
    }
    internal const string Start = "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg-stream\",\"content\":[]}}\n\n";
    internal static IAsyncEnumerable<ClaudeMessageEvent> Enumerate(GoogleVertexLlmProvider provider, CancellationToken token) => ((IClaudeMessagesAdapter)provider).StreamAsync(ClaudeFixture.Request(), ClaudeFixture.Context(), token);
}
