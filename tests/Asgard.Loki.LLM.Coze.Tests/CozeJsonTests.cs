using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Coze.Tests.CozeFixture;

namespace Asgard.Loki.LLM.Coze.Tests;

/// <summary>每个原生方法只有一次 HTTP，回执、状态、全部消息与独立字面样本一致。</summary>
public sealed class CozeJsonTests
{
    /// <summary>完整原生历史、工具关联、对象字符串和未来参数无损，回执不能伪装成模型完成或触发轮询。</summary>
    [Fact]
    public async Task Create_RetainsFullWireAndAcknowledgementWithoutPolling()
    {
        var body = new CozeBodyStream(Encoding.UTF8.GetBytes(Part("ack").GetRawText()));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal(Part("create_endpoint").GetString(), message.RequestUri!.OriginalString);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(Token, message.Headers.Authorization.Parameter);
            var actual = JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)); Assert.True(JsonElement.DeepEquals(Part("wire_complete"), actual));
            Assert.False(actual.GetProperty("auto_save_history").GetBoolean()); Assert.False(actual.TryGetProperty("model", out _)); Assert.False(actual.TryGetProperty("conversation_id", out _));
            Assert.Equal("9007199254740993", actual.GetProperty("parameters").GetProperty("exact").GetRawText());
            return new(HttpStatusCode.OK) { Content = new StreamContent(body) };
        });
        using var factory = new CozeHttpFactory(handler) { DefaultBearer = "fixture-other-default" }; var client = new CozeChatClient(factory, Options(), Token); var request = Request(); var before = JsonSerializer.Serialize(request);
        var result = await client.CreateAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(Part("ack").GetRawText(), result.Data.GetRawText()); Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.Equal("in_progress", result.Status); Assert.False(result.IsChatTerminal);
        Assert.Equal("create", result.Operation); Assert.Equal(Part("chat_id").GetString(), result.Reference.ChatId); Assert.Equal(Part("conversation_id").GetString(), result.Reference.ConversationId);
        Assert.Equal(Part("bot_id").GetString(), result.Metadata.BotId); Assert.DoesNotContain(Token, JsonSerializer.Serialize(result)); Assert.DoesNotContain(Token, JsonSerializer.Serialize(client.Metadata));
        Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>引用可持久化并恢复；查询和消息读取各一次 GET，保留待工具状态、所有回答/中间消息及原始顺序。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExplicitQuery_RetainsFullNativeDataAndReference(bool messages)
    {
        var reference = JsonSerializer.Deserialize<CozeChatReference>(JsonSerializer.Serialize(await Reference()))!; var expected = Part(messages ? "messages" : "retrieved"); var body = new CozeBodyStream(Encoding.UTF8.GetBytes(expected.GetRawText()));
        using var handler = new StubHttpHandler((message, _) =>
        {
            Assert.Equal(HttpMethod.Get, message.Method); Assert.Null(message.Content); Assert.Equal(Part(messages ? "messages_endpoint" : "retrieve_endpoint").GetString(), message.RequestUri!.OriginalString);
            Assert.Equal(Token, message.Headers.Authorization!.Parameter); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        });
        using var factory = new CozeHttpFactory(handler); var client = new CozeChatClient(factory, Options(), Token); var result = messages ? await client.GetMessagesAsync(reference, TestContext.Current.CancellationToken) : await client.RetrieveAsync(reference, TestContext.Current.CancellationToken);
        Assert.Equal(expected.GetRawText(), result.Data.GetRawText()); Assert.Equal(reference, result.Reference); Assert.Equal(messages ? "messages" : "retrieve", result.Operation);
        if (messages) { Assert.Null(result.Status); Assert.Equal(5, result.Data.GetProperty("data").GetArrayLength()); Assert.Equal("message/answer-a", result.Data.GetProperty("data")[2].GetProperty("id").GetString()); }
        else { Assert.True(result.IsChatTerminal); Assert.Equal("requires_action", result.Status); Assert.Equal(2, result.Data.GetProperty("data").GetProperty("required_action").GetProperty("submit_tool_outputs").GetProperty("tool_calls").GetArrayLength()); }
        Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>客户端目标固定，底层模型不被伪造；没有会话 ID 时只让原生服务分配，不填充 URL 查询或参数默认值。</summary>
    [Fact]
    public async Task MinimalCreate_PreservesOmissionAndServerAssignedConversation()
    {
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://native.invalid/tenant/v3/chat", message.RequestUri!.OriginalString);
            var data = JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)); Assert.Equal(3, data.EnumerateObject().Count());
            Assert.Equal("native-user", data.GetProperty("user_id").GetString()); Assert.False(data.TryGetProperty("auto_save_history", out _)); Assert.False(data.TryGetProperty("additional_messages", out _));
            return new(HttpStatusCode.OK) { Content = new StringContent(Part("ack").GetRawText(), Encoding.UTF8, "application/json") };
        });
        using var factory = new CozeHttpFactory(handler); var client = new CozeChatClient(factory, Options(), Token); var result = await client.CreateAsync(new() { UserId = "native-user" }, TestContext.Current.CancellationToken);
        Assert.Equal(Part("conversation_id").GetString(), result.Reference.ConversationId); Assert.Equal(1, handler.Calls);
    }
    /// <summary>直接客户端在工厂创建前冻结全部原生参数，不借用调用者容器。</summary>
    [Fact]
    public async Task NativeSnapshot_PrecedesFactoryAndFirstAwait()
    {
        var request = Request(); using var handler = new StubHttpHandler(async (message, token) =>
        { Assert.True(JsonElement.DeepEquals(Part("wire_complete"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return new(HttpStatusCode.OK) { Content = new StringContent(Part("ack").GetRawText(), Encoding.UTF8, "application/json") }; });
        using var factory = new CozeHttpFactory(handler) { Configure = _ => request.Parameters.Clear() }; await new CozeChatClient(factory, Options(), Token).CreateAsync(request, TestContext.Current.CancellationToken);
        Assert.Empty(request.Parameters); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生失败/取消/未知状态保持原文，API code=0 不自动变成回答成功。</summary>
    [Theory] [InlineData("failed", true)] [InlineData("canceled", true)] [InlineData("requires_action", true)] [InlineData("completed", true)] [InlineData("future_state", false)]
    public async Task NativeStatus_IsNotNormalized(string status, bool terminal)
    {
        var text = Part("ack").GetRawText().Replace("in_progress", status, StringComparison.Ordinal); using var handler = Reply(new CozeBodyStream(Encoding.UTF8.GetBytes(text))); using var factory = new CozeHttpFactory(handler);
        var result = await new CozeChatClient(factory, Options(), Token).CreateAsync(Request(), TestContext.Current.CancellationToken); Assert.Equal(text, result.Data.GetRawText()); Assert.Equal(status, result.Status); Assert.Equal(terminal, result.IsChatTerminal);
    }
}
