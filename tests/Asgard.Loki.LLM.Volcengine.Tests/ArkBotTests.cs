using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Volcengine.Tests.ArkFixture;
using static Asgard.Loki.LLM.Volcengine.Tests.ArkBotFixture;

namespace Asgard.Loki.LLM.Volcengine.Tests;

/// <summary>固定发布应用的全量原生调用，不将 Bot 身份、实际模型和插件数据混成一个模型 Session。</summary>
public sealed class ArkBotTests
{
    /// <summary>JSON/SSE 完整 wire 与字面窗口一致，实际返回模型与多端点/插件用量保持原文。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task WholeNativeBotData_AndHonestApplicationIdentityArePreserved(bool stream)
    {
        var body = new OllamaBodyStream(Bytes(stream ? Text("bot-events.sse") : BotPart("result").GetRawText()));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(BotPart("endpoint").GetString(), message.RequestUri!.OriginalString); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal(Key, message.Headers.Authorization!.Parameter);
            var expected = JsonNode.Parse(BotPart("wire").GetRawText())!.AsObject(); expected["stream"] = stream; Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream);
        }); using var factory = new ArkHttpFactory(handler); var client = new VolcengineBotClient(factory, BotOptions(), Key);
        if (stream)
        {
            var events = await BotStream(client, TestContext.Current.CancellationToken, item => { if (item.IsTerminal || item.Json.HasValue && Ending(item.Json.Value)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } });
            var expected = BotFrames(); Assert.Equal(expected.Count + 1, events.Count);
            for (var i = 0; i < events.Count; i++) { Assert.Equal("transport-bot", events[i].EventId); Assert.Equal(0L, events[i].RetryMilliseconds); Assert.Equal(i == 0 ? "native.bot" : null, events[i].EventName); Assert.Equal(client.Metadata, events[i].Metadata); Assert.Equal(i == expected.Count, events[i].IsTerminal); if (i < expected.Count) { Assert.Equal(expected[i], events[i].Data); Assert.Equal(expected[i], events[i].Json!.Value.GetRawText()); } }
            Assert.Equal("[DONE]", events[^1].Data); Assert.Null(events[^1].Json); Assert.False(events[2].IsTerminal); Assert.Equal(2, events[3].Json!.Value.GetProperty("bot_usage").GetProperty("model_usage").GetArrayLength());
        }
        else
        {
            var payload = BotPayload(); var before = payload.ToJsonString(); var result = await client.CompleteAsync(payload, TestContext.Current.CancellationToken);
            Assert.Equal(BotPart("result").GetRawText(), result.Data.GetRawText()); Assert.Equal(before, payload.ToJsonString()); Assert.Equal(client.Metadata, result.Metadata); Assert.NotEqual(client.Metadata.BotId, result.Data.GetProperty("model").GetString()); Assert.Equal(2, result.Data.GetProperty("bot_usage").GetProperty("model_usage").GetArrayLength()); Assert.Equal("ref-b", result.Data.GetProperty("references")[0].GetProperty("id").GetString());
            Assert.DoesNotContain(Key, JsonSerializer.Serialize(result)); Assert.DoesNotContain(Key, JsonSerializer.Serialize(client.Metadata));
        }
        Assert.Equal("published-app-selector", client.Metadata.BotId); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>没有 Bot 前缀也只访问明确 Bot 根；未提供原生可选参数时不补历史/用户/工具默认值。</summary>
    [Fact]
    public async Task MinimalBotCall_DoesNotGuessModelOrDefaults()
    {
        using var handler = new StubHttpHandler(async (message, token) => { Assert.Equal(BotPart("endpoint").GetString(), message.RequestUri!.OriginalString); var data = JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)); Assert.Equal(3, data.EnumerateObject().Count()); Assert.Equal(BotPart("bot_id").GetString(), data.GetProperty("model").GetString()); Assert.True(data.TryGetProperty("messages", out _)); Assert.False(data.TryGetProperty("user", out _)); return Reply(new MemoryStream(Bytes(BotPart("result").GetRawText())), false); });
        using var factory = new ArkHttpFactory(handler); await new VolcengineBotClient(factory, BotOptions(), Key).CompleteAsync(new() { ["messages"] = new JsonArray() }, TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>工厂边界之前冻结完整原生消息和配置，调用者容器改变不能改发出的请求。</summary>
    [Fact]
    public async Task BotSnapshot_PrecedesFactoryCallback()
    {
        var payload = BotPayload(); using var handler = new StubHttpHandler(async (message, token) => { Assert.True(JsonElement.DeepEquals(BotPart("wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new MemoryStream(Bytes(BotPart("result").GetRawText())), false); });
        using var factory = new ArkHttpFactory(handler) { Configure = _ => payload.Clear() }; await new VolcengineBotClient(factory, BotOptions(), Key).CompleteAsync(payload, TestContext.Current.CancellationToken); Assert.Empty(payload); Assert.Equal(1, handler.Calls);
    }
    /// <summary>Chat 数据里同名 Responses 业务类型不能冒充 Bot 全局终态，仍需真实 DONE。</summary>
    [Fact]
    public async Task ResponsesNamedFutureBotEvent_IsNotANativeFailure()
    {
        const string future = "{\"type\":\"response.failed\",\"response\":{\"id\":\"business\",\"status\":\"failed\"}}"; var frames = BotFrames(); frames.Add(future);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Wire(frames, true))), true))); using var factory = new ArkHttpFactory(handler); var events = await BotStream(new(factory, BotOptions(), Key), TestContext.Current.CancellationToken);
        Assert.Equal(future, events[^2].Data); Assert.False(events[^2].IsTerminal); Assert.True(events[^1].IsTerminal);
    }
}
