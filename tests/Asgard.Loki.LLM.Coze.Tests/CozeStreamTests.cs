using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Coze.Tests.CozeFixture;

namespace Asgard.Loki.LLM.Coze.Tests;

/// <summary>完整原生双层对话事件与字面窗口比较，不汇总使用量、工具或多条答案。</summary>
public sealed class CozeStreamTests
{
    /// <summary>所有已知 JSON、原生事件/ID/retry、未知不透明数据和 quoted DONE 都保持原文，终态在完整关闭后才可见。</summary>
    [Fact]
    public async Task NativeStream_MatchesEveryIndependentFrameAndIdentity()
    {
        var body = new CozeBodyStream(Encoding.UTF8.GetBytes(Text("events.sse"))); using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Part("create_endpoint").GetString(), message.RequestUri!.OriginalString); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal(Token, message.Headers.Authorization!.Parameter);
            Assert.True(JsonElement.DeepEquals(Part("wire_stream"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token))));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new("text/event-stream"); return response;
        });
        using var factory = new CozeHttpFactory(handler) { DefaultBearer = "fixture-unselected-default" }; var client = new CozeChatClient(factory, Options(), Token); var request = Request(); var before = JsonSerializer.Serialize(request);
        using var expected = JsonDocument.Parse(Text("events.json")); List<CozeChatEvent> events = [];
        await foreach (var item in client.StreamAsync(request, TestContext.Current.CancellationToken))
        {
            if (item.IsChatTerminal || item.IsTerminal) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); }
            events.Add(item);
        }
        Assert.Equal(expected.RootElement.GetArrayLength(), events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            var golden = expected.RootElement[i]; var actual = events[i]; Assert.Equal(golden.GetProperty("data").GetString(), actual.Data);
            Assert.Equal(golden.GetProperty("event").GetString(), actual.EventName); Assert.Equal(golden.GetProperty("id").GetString(), actual.EventId); Assert.Equal(golden.GetProperty("retry").GetInt64(), actual.RetryMilliseconds);
            Assert.Equal(i == 8, actual.IsChatTerminal); Assert.Equal(i == 10, actual.IsTerminal); Assert.Equal(Part("chat_id").GetString(), actual.Reference!.ChatId); Assert.Equal(client.Metadata, actual.Metadata);
            if (i < 9) Assert.Equal(actual.Data, actual.Json!.Value.GetRawText());
        }
        Assert.Null(events[9].Json); Assert.Equal(JsonValueKind.String, events[10].Json!.Value.ValueKind); Assert.Equal("[DONE]", events[10].Json!.Value.GetString());
        Assert.False(events[5].IsChatTerminal); Assert.False(events[7].IsChatTerminal); Assert.NotEqual(events[5].Json!.Value.GetProperty("id").GetString(), events[5].Reference!.ChatId);
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.Equal(1, handler.Calls); Assert.False(factory.Disposed);
    }
    /// <summary>两种文档中的实际 DONE 字面及空生成都保持原生，无需伪造 JSON 或回答文本。</summary>
    [Theory] [InlineData("[DONE]")] [InlineData("\"[DONE]\"")]
    public async Task BothNativeDoneLexicalForms_AreRetained(string data)
    {
        var frames = CozeStreamFixture.Events(); frames = [frames[0], frames[8], ("done", data)];
        using var handler = Reply(new CozeBodyStream(Encoding.UTF8.GetBytes(CozeStreamFixture.Wire(frames))), "text/event-stream"); using var factory = new CozeHttpFactory(handler); var client = new CozeChatClient(factory, Options(), Token); List<CozeChatEvent> events = [];
        await foreach (var item in client.StreamAsync(Request(), TestContext.Current.CancellationToken)) events.Add(item);
        Assert.Equal(3, events.Count); Assert.Equal(data, events[^1].Data); Assert.Equal(data[0] == '"', events[^1].Json.HasValue); Assert.True(events[^1].IsTerminal);
    }
    /// <summary>失败和待工具输入是原生 Chat 结果，不被映射为 stop 或自动执行工具；错误终态可保留原生 code/msg 载荷。</summary>
    [Theory] [InlineData("failed")] [InlineData("requires_action")] [InlineData("failed-code-payload")]
    public async Task NativeUnsuccessfulOutcomes_RemainVisible(string status)
    {
        var frames = CozeStreamFixture.Events(); var data = status == "failed-code-payload" ? "{\"code\":701231,\"msg\":\"native failure\",\"future\":null}" : Part("retrieved").GetProperty("data").GetRawText().Replace("requires_action", status, StringComparison.Ordinal);
        frames[8] = (status == "requires_action" ? "conversation.chat.requires_action" : "conversation.chat.failed", data);
        using var handler = Reply(new CozeBodyStream(Encoding.UTF8.GetBytes(CozeStreamFixture.Wire(frames))), "text/event-stream"); var client = new CozeChatClient(new CozeHttpFactory(handler), Options(), Token); List<CozeChatEvent> events = [];
        await foreach (var item in client.StreamAsync(Request(), TestContext.Current.CancellationToken)) events.Add(item);
        Assert.True(events[8].IsChatTerminal); Assert.Equal(data, events[8].Data); Assert.True(events[^1].IsTerminal); Assert.Equal(1, handler.Calls);
    }
    /// <summary>消息 ID 即使偶然与 Chat ID 相同，也仍按不同原生层级解释，不相互覆盖。</summary>
    [Fact]
    public async Task MessageIdAndChatId_HaveIndependentIdentityDomains()
    {
        var frames = CozeStreamFixture.Events(); foreach (var i in new[] { 4, 5 }) CozeStreamFixture.Change(frames, i, "id", Part("chat_id").GetString()!);
        using var handler = Reply(new CozeBodyStream(Encoding.UTF8.GetBytes(CozeStreamFixture.Wire(frames))), "text/event-stream"); var client = new CozeChatClient(new CozeHttpFactory(handler), Options(), Token); List<CozeChatEvent> events = [];
        await foreach (var item in client.StreamAsync(Request(), TestContext.Current.CancellationToken)) events.Add(item);
        Assert.Equal(11, events.Count); Assert.Equal(Part("chat_id").GetString(), events[4].Json!.Value.GetProperty("id").GetString()); Assert.True(events[^1].IsTerminal);
    }
    /// <summary>流式原生引用可用于后续显式单次查询，但流方法自身不追加任何请求。</summary>
    [Fact]
    public async Task NativeStreamReference_RequiresExplicitFollowupCall()
    {
        using var handler = new StubHttpHandler((message, _) =>
        {
            var stream = message.Method == HttpMethod.Post;
            if (!stream) Assert.Equal(Part("retrieve_endpoint").GetString(), message.RequestUri!.OriginalString);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream ? Text("events.sse") : Part("retrieved").GetRawText(), Encoding.UTF8, stream ? "text/event-stream" : "application/json") });
        });
        using var factory = new CozeHttpFactory(handler); var client = new CozeChatClient(factory, Options(), Token); CozeChatReference? reference = null;
        await foreach (var item in client.StreamAsync(Request(), TestContext.Current.CancellationToken)) reference = item.Reference;
        Assert.Equal(1, handler.Calls); Assert.NotNull(reference); var result = await client.RetrieveAsync(reference, TestContext.Current.CancellationToken); Assert.Equal("requires_action", result.Status); Assert.Equal(2, handler.Calls);
    }
    /// <summary>首个已知 Chat 身份前的未来元数据不制造引用；已知 Message 可省略单流内无歧义的重复关联字段。</summary>
    [Fact]
    public async Task UnknownPrefixAndOmittedRepeatedIds_AreRetainedWithoutSynthesis()
    {
        var frames = CozeStreamFixture.Events(); var data = System.Text.Json.Nodes.JsonNode.Parse(frames[4].Data)!.AsObject(); data.Remove("chat_id"); data.Remove("conversation_id"); frames[4] = (frames[4].Name, data.ToJsonString()); frames.Insert(0, ("future.prefix", "opaque-before-chat"));
        using var handler = Reply(new CozeBodyStream(Encoding.UTF8.GetBytes(CozeStreamFixture.Wire(frames))), "text/event-stream"); var client = new CozeChatClient(new CozeHttpFactory(handler), Options(), Token); List<CozeChatEvent> events = [];
        await foreach (var item in client.StreamAsync(Request(), TestContext.Current.CancellationToken)) events.Add(item);
        Assert.Null(events[0].Reference); Assert.Null(events[0].Json); Assert.Equal("opaque-before-chat", events[0].Data); Assert.False(events[5].Json!.Value.TryGetProperty("chat_id", out _)); Assert.True(events[^1].IsTerminal);
    }
}
