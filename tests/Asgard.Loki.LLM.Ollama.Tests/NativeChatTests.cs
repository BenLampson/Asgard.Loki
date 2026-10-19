using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Ollama.Tests;

/// <summary>公共 SDK 的完整独立金样验证原生字段、工具关联、模型覆盖和流式记录。</summary>
public sealed class NativeChatTests
{
    /// <summary>只覆盖实际模型和方法流标志，其他字段完整保真；匿名与 Bearer 均明确选择。</summary>
    [Theory] [InlineData(true, true)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(false, false)]
    public async Task CompleteWireAndResults_AreNative(bool stream, bool anonymous)
    {
        var expected = JsonNode.Parse(OllamaFixture.Text("wire"))!.AsObject(); expected["stream"] = stream;
        var events = JsonSerializer.Deserialize<JsonElement[]>(OllamaFixture.Text("events"))!;
        var responseText = stream ? string.Join("\r\n", events.Select(e => e.GetRawText())) + "\r\n" : OllamaFixture.Text("response");
        var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(responseText));
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("http://native.invalid/prefix/api/chat", request.RequestUri!.AbsoluteUri);
            Assert.Equal(anonymous ? null : "Bearer fixture-secret", request.Headers.Authorization?.ToString());
            Assert.Equal(stream ? "application/x-ndjson" : "application/json", Assert.Single(request.Headers.Accept).MediaType);
            var actual = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)); Assert.True(JsonNode.DeepEquals(expected, actual));
            Assert.Equal("call-native", actual!["messages"]![3]!["tool_call_id"]!.GetValue<string>());
            Assert.Equal("{\"z\":9007199254740993,\"a\":false,\"nested\":{\"x\":null}}", actual["messages"]![2]!["tool_calls"]![0]!["function"]!["arguments"]!.ToJsonString());
            var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            reply.Content.Headers.ContentType = new(stream ? "application/x-ndjson" : "application/json"); return reply;
        });
        var factory = new OllamaHttpFactory(handler); var provider = new OllamaLlmProvider(factory);
        await using (var client = OllamaFixture.Client(provider, anonymous))
        {
            if (stream)
            {
                var index = 0;
                await foreach (var item in client.LLM.OllamaChat.StreamAsync(OllamaFixture.Request(), new() { SessionId = "session" }, TestContext.Current.CancellationToken))
                {
                    Assert.True(JsonElement.DeepEquals(events[index], item.Data)); Assert.Null(item.EventId); Assert.Null(item.EventName); Assert.Null(item.RetryMilliseconds); Assert.Null(item.Id);
                    Assert.Equal(index == events.Length - 1, item.IsTerminal);
                    Assert.Equal(item.IsTerminal, body.Eof); Assert.Equal(item.IsTerminal, body.Disposed); Assert.Equal(item.IsTerminal, Assert.Single(factory.Clients).Disposed);
                    index++;
                }
                Assert.Equal(events.Length, index);
            }
            else
            {
                var result = await client.LLM.OllamaChat.CompleteAsync(OllamaFixture.Request(), new() { SessionId = "session" }, TestContext.Current.CancellationToken);
                Assert.True(OllamaFixture.Same(OllamaFixture.Text("response"), result.Data)); Assert.True(result.Done); Assert.Null(result.Id);
                Assert.False(result.Data.TryGetProperty("usage", out _)); Assert.False(result.Data.TryGetProperty("choices", out _));
                Assert.Equal("{\"b\":2,\"a\":9007199254740993}", result.Message!.Value.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetRawText());
                Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
            }
        }
        Assert.Equal(1, handler.Calls); Assert.False(factory.Disposed); Assert.Equal("Asgard.Loki.LLM.Ollama", Assert.Single(factory.Names));
    }
    /// <summary>原生空消息 load/unload 仍允许单个 JSON 终态，保留空角色与字面原因。</summary>
    [Theory] [InlineData("load")] [InlineData("unload")] [InlineData("future_reason")]
    public async Task SingleJsonTerminal_IsPreserved(string reason)
    {
        var text = "{\"model\":\"native\",\"message\":{\"role\":\"\",\"content\":\"\"},\"done\":true,\"done_reason\":\"" + reason + "\"}";
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Empty(JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!["messages"]!.AsArray());
            return new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        });
        await using var client = OllamaFixture.Client(new(new StubHttpClientFactory(handler)));
        var items = new List<OllamaChatEvent>(); await foreach (var item in client.LLM.OllamaChat.StreamAsync(new() { Model = "public", Messages = [] }, new() { SessionId = "load" }, TestContext.Current.CancellationToken)) items.Add(item);
        Assert.True(OllamaFixture.Same(text, Assert.Single(items).Data)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>think 的布尔、字符串与 null 均原样发送，不探测模型、不猜预算或替换原生 schema。</summary>
    [Theory] [InlineData("true")] [InlineData("false")] [InlineData("null")] [InlineData("\"low\"")] [InlineData("\"future-level\"")]
    public async Task ReasoningAndSchema_AreNative(string think)
    {
        var request = OllamaFixture.Request(); request.Parameters["think"] = JsonSerializer.Deserialize<JsonElement>(think);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var wire = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!;
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(think), wire["think"]));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(OllamaFixture.Text("wire"))!["format"], wire["format"]));
            return new(HttpStatusCode.OK) { Content = new StringContent(OllamaFixture.Terminal, System.Text.Encoding.UTF8, "application/json") };
        });
        await using var client = OllamaFixture.Client(new(new StubHttpClientFactory(handler)));
        await client.LLM.OllamaChat.CompleteAsync(request, new() { SessionId = "reasoning" }, TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>匿名目标不会继承工厂的默认 Key；显式 Key 目标使用本次选中的身份。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task DefaultAuthorization_CannotChangeSelectedIdentity(bool anonymous)
    {
        using var handler = new StubHttpHandler((request, _) =>
        { Assert.Equal("Bearer fixture-secret", request.Headers.Authorization!.ToString()); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OllamaFixture.Terminal, System.Text.Encoding.UTF8, "application/json") }); });
        var factory = new OllamaHttpFactory(handler) { DefaultBearer = "unselected-key" }; await using var client = OllamaFixture.Client(new(factory), anonymous);
        if (anonymous) await Assert.ThrowsAsync<InvalidOperationException>(() => client.LLM.OllamaChat.CompleteAsync(OllamaFixture.Request(), new() { SessionId = "auth" }, TestContext.Current.CancellationToken));
        else await client.LLM.OllamaChat.CompleteAsync(OllamaFixture.Request(), new() { SessionId = "auth" }, TestContext.Current.CancellationToken);
        Assert.Equal(anonymous ? 0 : 1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed);
    }
}
