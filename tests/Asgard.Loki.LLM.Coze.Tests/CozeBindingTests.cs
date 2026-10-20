using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Coze.Tests.CozeFixture;

namespace Asgard.Loki.LLM.Coze.Tests;

/// <summary>原生服务引用不重选账号/智能体/凭据，所有调用者输入先验证再等待。</summary>
public sealed class CozeBindingTests
{
    /// <summary>完整目标身份的每个变化都必须拒绝旧引用，不能借同 Chat ID 查询其他服务。</summary>
    [Theory] [InlineData("root")] [InlineData("bot")] [InlineData("target")] [InlineData("credential")] [InlineData("version")] [InlineData("client")]
    [InlineData("token")] [InlineData("request")] [InlineData("response")] [InlineData("stream")] [InlineData("tail")] [InlineData("messages")]
    public async Task TargetDrift_RejectsReferenceBeforeClientCreation(string kind)
    {
        var reference = await Reference(); var options = kind switch
        {
            "root" => Options() with { BaseAddress = new("https://other.invalid/v3/") }, "bot" => Options() with { BotId = "other-bot" },
            "target" => Options() with { TargetId = "other-target" }, "credential" => Options() with { CredentialId = "other-key-id" },
            "version" => Options() with { Version = "2" }, "client" => Options() with { HttpClientName = "other-client" },
            "request" => Options() with { MaximumRequestBytes = 20000 }, "response" => Options() with { MaximumResponseBytes = 20000 },
            "stream" => Options() with { MaximumStreamBytes = 20000 }, "tail" => Options() with { MaximumBufferedTailFrames = 3 },
            "messages" => Options() with { MaximumTrackedMessages = 3 }, _ => Options()
        };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected")); using var factory = new CozeHttpFactory(handler); var client = new CozeChatClient(factory, options, kind == "token" ? "fixture-other-token" : Token);
        await Assert.ThrowsAsync<ArgumentException>(() => client.RetrieveAsync(reference, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetMessagesAsync(reference, TestContext.Current.CancellationToken)); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>伪造/空身份、坏 Unicode 或缺失原生 ID 在派发前失败，引用中的值不是 URL。</summary>
    [Theory] [InlineData("fingerprint")] [InlineData("empty-chat")] [InlineData("empty-conversation")] [InlineData("unicode-chat")] [InlineData("unicode-conversation")]
    public async Task InvalidReference_FailsBeforeHttp(string kind)
    {
        var reference = await Reference(); var invalid = new string((char)0xd800, 1); reference = kind switch
        { "fingerprint" => reference with { TargetIdentity = "different" }, "empty-chat" => reference with { ChatId = "" }, "empty-conversation" => reference with { ConversationId = " " }, "unicode-chat" => reference with { ChatId = invalid }, _ => reference with { ConversationId = invalid } };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected")); using var factory = new CozeHttpFactory(handler); var client = new CozeChatClient(factory, Options(), Token);
        Assert.NotNull(await Record.ExceptionAsync(() => client.RetrieveAsync(reference, TestContext.Current.CancellationToken))); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>只拒绝 SDK 拥有的原生路径和坏 Unicode，不替换显式用户或保存历史选项。</summary>
    [Theory] [InlineData("bot_id")] [InlineData("BOT_ID")] [InlineData("user_id")] [InlineData("USER_ID")] [InlineData("stream")] [InlineData("STREAM")]
    [InlineData("conversation_id")] [InlineData("CONVERSATION_ID")] [InlineData("undefined")] [InlineData("user-empty")] [InlineData("user-unicode")]
    [InlineData("conversation-empty")] [InlineData("conversation-unicode")] [InlineData("value-unicode")] [InlineData("key-unicode")]
    public async Task InvalidCreateInput_DoesNotTouchFactory(string kind)
    {
        var request = Request(); var invalid = new string((char)0xd800, 1);
        if (kind == "user-empty") request = request with { UserId = " " }; else if (kind == "user-unicode") request = request with { UserId = invalid };
        else if (kind == "conversation-empty") request = request with { ConversationId = "" }; else if (kind == "conversation-unicode") request = request with { ConversationId = invalid };
        else if (kind is "value-unicode" or "key-unicode") { using var doc = JsonDocument.Parse(kind == "value-unicode" ? "{\"x\":\"\\ud800\"}" : "{\"\\ud800\":null}"); request.Parameters["future"] = doc.RootElement.Clone(); }
        else request.Parameters[kind] = kind == "undefined" ? default : JsonSerializer.SerializeToElement(false);
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected")); using var factory = new CozeHttpFactory(handler); var client = new CozeChatClient(factory, Options(), Token);
        Assert.NotNull(await Record.ExceptionAsync(() => client.CreateAsync(request, TestContext.Current.CancellationToken))); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>另一种默认 Key 不能附带到明确选中身份，已有默认 Authorization 则被选中 Bearer 覆盖。</summary>
    [Theory] [InlineData("api-key")] [InlineData("x-api-key")] [InlineData("x-goog-api-key")]
    public async Task UnboundDefaultAuthentication_IsRejected(string name)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected")); using var factory = new CozeHttpFactory(handler) { Configure = client => client.DefaultRequestHeaders.TryAddWithoutValidation(name, "fixture-default") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CozeChatClient(factory, Options(), Token).CreateAsync(Request(), TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>回执/查询/消息中的关联必须匹配；不能把 Message.id 当 Chat ID 或静默重绑定。</summary>
    [Theory] [InlineData("create", "bot_id")] [InlineData("create", "conversation_id")] [InlineData("retrieve", "id")]
    [InlineData("retrieve", "conversation_id")] [InlineData("messages", "chat_id")] [InlineData("messages", "conversation_id")] [InlineData("messages", "bot_id")]
    public async Task NativeEchoMismatch_FailsExplicitly(string operation, string field)
    {
        var reference = await Reference(); var native = System.Text.Json.Nodes.JsonNode.Parse(Part(operation == "create" ? "ack" : operation == "messages" ? "messages" : "retrieved").GetRawText())!.AsObject();
        var data = native["data"]!; if (operation == "messages") data = data[0]!; data[field] = "other-native-id";
        using var handler = Reply(new CozeBodyStream(Encoding.UTF8.GetBytes(native.ToJsonString()))); using var factory = new CozeHttpFactory(handler); var client = new CozeChatClient(factory, Options(), Token);
        async Task Invoke() { if (operation == "create") await client.CreateAsync(Request(), TestContext.Current.CancellationToken); else if (operation == "retrieve") await client.RetrieveAsync(reference, TestContext.Current.CancellationToken); else await client.GetMessagesAsync(reference, TestContext.Current.CancellationToken); }
        await Assert.ThrowsAsync<InvalidDataException>(Invoke); Assert.Equal(1, handler.Calls);
    }
    /// <summary>非法原生根、身份或全部资源预算在客户端构造时拒绝。</summary>
    [Theory] [InlineData("query")] [InlineData("user")] [InlineData("fragment")] [InlineData("relative")] [InlineData("bot-empty")] [InlineData("version-empty")]
    [InlineData("request-zero")] [InlineData("response-zero")] [InlineData("stream-zero")] [InlineData("tail-zero")] [InlineData("messages-zero")]
    [InlineData("identity-unicode")] [InlineData("bot-unicode")] [InlineData("client-unicode")] [InlineData("token-control")]
    public void InvalidOptions_AreRejectedBeforeFactory(string kind)
    {
        var invalid = new string((char)0xd800, 1); var options = kind switch
        {
            "query" => Options() with { BaseAddress = new("https://native.invalid/v3/?x=1") }, "user" => Options() with { BaseAddress = new("https://fixture@native.invalid/v3/") },
            "fragment" => Options() with { BaseAddress = new("https://native.invalid/v3/#x") }, "relative" => Options() with { BaseAddress = new("v3/", UriKind.Relative) },
            "bot-empty" => Options() with { BotId = "" }, "version-empty" => Options() with { Version = "" },
            "request-zero" => Options() with { MaximumRequestBytes = 0 }, "response-zero" => Options() with { MaximumResponseBytes = 0 }, "stream-zero" => Options() with { MaximumStreamBytes = 0 },
            "tail-zero" => Options() with { MaximumBufferedTailFrames = 0 }, "messages-zero" => Options() with { MaximumTrackedMessages = 0 },
            "identity-unicode" => Options() with { TargetId = invalid }, "bot-unicode" => Options() with { BotId = invalid }, "client-unicode" => Options() with { HttpClientName = invalid }, _ => Options()
        };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected")); using var factory = new CozeHttpFactory(handler);
        Assert.NotNull(Record.Exception(() => new CozeChatClient(factory, options, kind == "token-control" ? "fixture\r\nkey" : Token))); Assert.Empty(factory.Clients);
    }
}
