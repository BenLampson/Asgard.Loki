using System.Text;
using System.Text.Json;
using Xunit;
using static Asgard.Loki.LLM.Dify.Tests.DifyFixture;

namespace Asgard.Loki.LLM.Dify.Tests;

/// <summary>入口和原生结果严格验证，不通过清理数据掩盖非法调用。</summary>
public sealed class DifyValidationTests
{
    /// <summary>原始坏字符串、扩展别名和原生保留字段在工厂之前失败。</summary>
    [Theory] [InlineData("user")] [InlineData("query")] [InlineData("inputs")] [InlineData("USER")] [InlineData("Query")] [InlineData("INPUTS")] [InlineData("response_mode")] [InlineData("Response_Mode")] [InlineData("Conversation_ID")] [InlineData("undefined")] [InlineData("unicode")] [InlineData("key-unicode")] [InlineData("input-unicode")] [InlineData("duplicate")] [InlineData("duplicate-input")] [InlineData("agent")]
    public async Task InvalidRequest_FailsBeforeFactory(string fault)
    {
        var request = Request(); var options = Options();
        switch (fault)
        {
            case "user": request = request with { User = " " }; break;
            case "query": request = request with { Query = null! }; break;
            case "inputs": request = request with { Inputs = Json("[]") }; break;
            case "unicode": request = request with { Query = new string((char)0xd800, 1) }; break;
            case "key-unicode": request.Parameters[new string((char)0xd800, 1)] = Json("null"); break;
            case "input-unicode": request = request with { Inputs = Json("{\"x\":\"\\ud800\"}") }; break;
            case "duplicate": request.Parameters["future"] = Json("{\"x\":1,\"x\":2}"); break;
            case "duplicate-input": request = request with { Inputs = Json("{\"x\":1,\"x\":2}") }; break;
            case "undefined": request.Parameters["future"] = default; break;
            case "agent": options = Options(DifyChatProfile.Agent); break;
            default: request.Parameters[fault] = Json("false"); break;
        }
        using var handler = Reply(Body()); using var factory = new DifyHttpFactory(handler);
        await Assert.ThrowsAnyAsync<Exception>(() => new DifyChatClient(factory, options, Token).SendAsync(request, TestContext.Current.CancellationToken)); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>固定目标、策略与凭据在调用前验证，不猜测应用类型。</summary>
    [Theory] [InlineData("uri")] [InlineData("query")] [InlineData("fragment")] [InlineData("userinfo")] [InlineData("profile")] [InlineData("key")] [InlineData("request")] [InlineData("response")] [InlineData("stream")] [InlineData("tail")]
    public void InvalidOptions_RejectWithoutHttp(string fault)
    {
        var options = Options(); var key = Token;
        options = fault switch { "uri" => options with { BaseAddress = new("file:///tmp") }, "query" => options with { BaseAddress = new("https://native.invalid/?x=1") }, "fragment" => options with { BaseAddress = new("https://native.invalid/#x") }, "userinfo" => options with { BaseAddress = new("https://user:pass@native.invalid/") }, "profile" => options with { Profile = 0 }, "request" => options with { MaximumRequestBytes = 0 }, "response" => options with { MaximumResponseBytes = 0 }, "stream" => options with { MaximumStreamBytes = 0 }, "tail" => options with { MaximumBufferedTailFrames = 0 }, _ => options };
        if (fault == "key") key = "invalid key";
        using var handler = Reply(Body()); using var factory = new DifyHttpFactory(handler); Assert.ThrowsAny<Exception>(() => new DifyChatClient(factory, options, key)); Assert.Empty(factory.Clients);
    }
    /// <summary>未知嵌套字段也严格 UTF-8/Unicode/重复属性；会话关联不能静默换绑。</summary>
    [Theory] [InlineData("conversation")] [InlineData("event")] [InlineData("message")] [InlineData("task")] [InlineData("answer")] [InlineData("array")] [InlineData("duplicate")] [InlineData("unicode")] [InlineData("bytes")] [InlineData("media")] [InlineData("charset")]
    public async Task InvalidResponse_RejectsWithoutRetry(string fault)
    {
        var data = Part("blocking").GetRawText();
        data = fault switch { "conversation" => data.Replace("conv/+&", "other", StringComparison.Ordinal), "event" => data.Replace("\"event\":\"message\"", "\"event\":\"future\"", StringComparison.Ordinal), "message" => data.Replace("\"message_id\":\"msg-a\"", "\"message_id\":null", StringComparison.Ordinal), "task" => data.Replace("\"task_id\":\"task-a\"", "\"task_id\":0", StringComparison.Ordinal), "answer" => data.Replace("\"answer\":\"<think>原文</think>回答\"", "\"answer\":false", StringComparison.Ordinal), "array" => "[]", "duplicate" => "{\"x\":1,\"x\":2}", "unicode" => "{\"x\":\"\\ud800\"}", _ => data };
        var bytes = fault == "bytes" ? new byte[] { 0xff } : Encoding.UTF8.GetBytes(data); var body = new DifyBodyStream(bytes);
        using var handler = Reply(body, fault == "media" ? "text/html" : fault == "charset" ? "application/json; charset=iso-8859-1" : "application/json"); using var factory = new DifyHttpFactory(handler);
        await Assert.ThrowsAnyAsync<Exception>(() => new DifyChatClient(factory, Options(), Token).SendAsync(Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>请求和响应预算、默认其他认证、方法/重定向和预取消都不能偷偷切换目标。</summary>
    [Theory] [InlineData("request")] [InlineData("response")] [InlineData("length")] [InlineData("auth")] [InlineData("method")] [InlineData("redirect")] [InlineData("cancel")]
    public async Task TransportGuards_AreSingleAttempt(string fault)
    {
        var body = Body(); using var handler = new Asgard.Loki.Testing.StubHttpHandler((message, _) =>
        {
            if (fault == "method") message.Method = HttpMethod.Get;
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(body) };
            if (fault == "redirect") response.RequestMessage = new(HttpMethod.Post, "https://other.invalid/chat-messages");
            if (fault == "length") response.Content.Headers.ContentLength = 1;
            return Task.FromResult(response);
        });
        using var factory = new DifyHttpFactory(handler) { Configure = c => { if (fault == "auth") c.DefaultRequestHeaders.Add("x-api-key", "unbound"); } };
        var options = fault == "request" ? Options() with { MaximumRequestBytes = 1 } : fault == "response" ? Options() with { MaximumResponseBytes = 1 } : Options();
        await Assert.ThrowsAnyAsync<Exception>(() => new DifyChatClient(factory, options, Token).SendAsync(Request(), fault == "cancel" ? new CancellationToken(true) : TestContext.Current.CancellationToken));
        Assert.Equal(fault is "request" or "auth" or "cancel" ? 0 : 1, handler.Calls); if (fault is "request" or "cancel") Assert.Empty(factory.Clients);
    }
}
