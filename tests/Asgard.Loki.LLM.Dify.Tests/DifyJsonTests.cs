using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Dify.Tests.DifyFixture;

namespace Asgard.Loki.LLM.Dify.Tests;

/// <summary>完整原生线协议、关联域与资源边界，调用次数不借助模型网络。</summary>
public sealed class DifyJsonTests
{
    /// <summary>原生请求/结果逐字段保持字面样本，不推断底层模型、上传文件或计算用量。</summary>
    [Fact]
    public async Task Send_RetainsCompleteWireAndResponse()
    {
        var body = Body(); using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal(Part("endpoint").GetString(), message.RequestUri!.OriginalString); Assert.Equal(Token, message.Headers.Authorization!.Parameter);
            var actual = JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)); Assert.True(JsonElement.DeepEquals(Part("wire_blocking"), actual));
            Assert.Equal("9007199254740993", actual.GetProperty("inputs").GetProperty("exact").GetRawText()); Assert.False(actual.TryGetProperty("model", out _));
            return new(HttpStatusCode.OK) { Content = new StreamContent(body) };
        });
        using var factory = new DifyHttpFactory(handler) { DefaultBearer = "another-default" }; var request = Request(); var before = JsonSerializer.Serialize(request);
        var client = new DifyChatClient(factory, Options(), Token); var result = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(Part("blocking").GetRawText(), result.Data.GetRawText()); Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.False(result.IsPaused);
        Assert.NotEqual(result.Data.GetProperty("id").GetString(), result.Data.GetProperty("message_id").GetString()); Assert.Equal(client.Metadata, result.Metadata);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result)); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>未给会话和可选参数时保持缺省，显式空/null 会话不被补成其他值。</summary>
    [Theory] [InlineData("omit")] [InlineData("empty")] [InlineData("null")]
    public async Task NativeOmissionAndEmptyConversation_AreDistinct(string kind)
    {
        var request = new DifyChatRequest { User = "explicit-user", Query = "", Inputs = Json("{}") };
        if (kind != "omit") request.Parameters["conversation_id"] = Json(kind == "null" ? "null" : "\"\"");
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var data = JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)); Assert.Equal(kind == "omit" ? 4 : 5, data.EnumerateObject().Count());
            Assert.False(data.TryGetProperty("auto_generate_name", out _)); Assert.False(data.TryGetProperty("files", out _)); Assert.Equal("", data.GetProperty("query").GetString());
            if (kind != "omit") Assert.Equal(kind == "null" ? JsonValueKind.Null : JsonValueKind.String, data.GetProperty("conversation_id").ValueKind);
            return new(HttpStatusCode.OK) { Content = new StringContent(Part("blocking").GetRawText(), Encoding.UTF8, "application/json") };
        });
        using var factory = new DifyHttpFactory(handler); await new DifyChatClient(factory, Options(), Token).SendAsync(request, TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>请求在首次工厂边界前冻结，可变调用者容器不能改变已经选择的线协议。</summary>
    [Fact]
    public async Task Snapshot_PrecedesFactory()
    {
        var request = Request(); using var handler = new StubHttpHandler(async (message, token) =>
        { Assert.True(JsonElement.DeepEquals(Part("wire_blocking"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return new(HttpStatusCode.OK) { Content = new StringContent(Part("blocking").GetRawText(), Encoding.UTF8, "application/json") }; });
        using var factory = new DifyHttpFactory(handler) { Configure = _ => request.Parameters.Clear() }; await new DifyChatClient(factory, Options(), Token).SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Empty(request.Parameters); Assert.Equal(1, handler.Calls);
    }
    /// <summary>暂停原文保持暂停，不变成成功回答或自动恢复/查询请求。</summary>
    [Fact]
    public async Task ChatflowPause_IsPreservedWithoutResume()
    {
        var data = Part("blocking").GetRawText().Replace("\"message\"", "\"workflow_paused\"", StringComparison.Ordinal)[..^1] + ",\"workflow_run_id\":\"run-paused\",\"data\":{\"status\":\"paused\",\"reasons\":[{\"form_id\":\"form-a\"}]}}";
        using var handler = Reply(Body(data)); using var factory = new DifyHttpFactory(handler); var result = await new DifyChatClient(factory, Options(DifyChatProfile.Chatflow), Token).SendAsync(Request(), TestContext.Current.CancellationToken);
        Assert.True(result.IsPaused); Assert.Equal(data, result.Data.GetRawText()); Assert.Equal(1, handler.Calls);
    }
    /// <summary>HTTP 与 HTTP200 原生错误保持原始字节/媒体/状态，不把嵌套错误误判。</summary>
    [Theory] [InlineData(400)] [InlineData(500)] [InlineData(200)]
    public async Task Errors_RetainNativeBytes(int status)
    {
        var data = Part("error").GetRawText(); var body = Body(data); using var handler = Reply(body, "application/problem+json; charset=utf-8", status); using var factory = new DifyHttpFactory(handler);
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => new DifyChatClient(factory, Options(), Token).SendAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(data, error.ResponseBody); Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>资源关闭/EOF/取消故障后不得交付成功结果。</summary>
    [Theory] [InlineData("read")] [InlineData("dispose")] [InlineData("cancel-eof")] [InlineData("cancel-body")] [InlineData("cancel-client")] [InlineData("client")] [InlineData("target")]
    public async Task LateFailure_DoesNotDeliverSuccess(string fault)
    {
        using var cancellation = new CancellationTokenSource(); var body = new DifyBodyStream(Encoding.UTF8.GetBytes(Part("blocking").GetRawText()))
        { FailAtEof = fault == "read", FailDispose = fault == "dispose", OnEof = fault == "cancel-eof" ? cancellation.Cancel : null, OnDispose = fault == "cancel-body" ? cancellation.Cancel : null };
        HttpRequestMessage? actual = null; using var handler = new StubHttpHandler((message, _) => { actual = message; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }); });
        using var factory = new DifyHttpFactory(handler) { OnClientDispose = () => { if (fault == "cancel-client") cancellation.Cancel(); if (fault == "client") throw new IOException("client close"); if (fault == "target") actual!.RequestUri = new("https://other.invalid/chat"); } };
        await Assert.ThrowsAnyAsync<Exception>(() => new DifyChatClient(factory, Options(), Token).SendAsync(Request(), cancellation.Token)); Assert.Equal(1, handler.Calls); Assert.True(body.Disposed);
    }
}
