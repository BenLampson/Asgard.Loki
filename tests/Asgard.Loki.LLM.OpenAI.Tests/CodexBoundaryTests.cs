using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.CodexFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>认证只在固定原生账户与主体内生效，坏请求在来源/网络之前失败。</summary>
public sealed class CodexBoundaryTests
{
    /// <summary>明确不支持的认证、目标/参数别名、坏 Unicode 和预算不能进入外部来源或 HTTP。</summary>
    [Theory] [InlineData("apikey")] [InlineData("anonymous")] [InlineData("provider")] [InlineData("credential")] [InlineData("root")] [InlineData("model")] [InlineData("alias-model")] [InlineData("alias-stream")] [InlineData("compact-stream")] [InlineData("unicode")] [InlineData("element")] [InlineData("duplicate")] [InlineData("request-limit")] [InlineData("cancel")] [InlineData("principal-unicode")]
    public async Task InvalidCall_FailsBeforeSourceAndFactory(string fault)
    {
        var payload = Payload(); var target = Target(); var credential = Credential(); var options = Options(); var compact = fault == "compact-stream";
        switch (fault)
        {
            case "principal-unicode": credential = LlmCredential.External("external-a", new string((char)0xd800, 1)); break;
            case "apikey": credential = new("external-a", "not-a-subscription-token"); break;
            case "anonymous": credential = LlmCredential.Anonymous("external-a"); break;
            case "provider": target = target with { ProviderId = "other" }; break;
            case "credential": target = target with { CredentialId = "other" }; break;
            case "root": target = target with { BaseAddress = new("https://backend.invalid/root?other=1") }; break;
            case "model": target = target with { UpstreamModel = new string((char)0xd800, 1) }; break;
            case "alias-model": payload["Model"] = "bad"; break;
            case "alias-stream": payload["Stream"] = true; break;
            case "compact-stream": payload["stream"] = false; break;
            case "unicode": payload["future"] = new string((char)0xd800, 1); break;
            case "element": payload["future"] = JsonValue.Create(JsonSerializer.Deserialize<JsonElement>("\"\\ud800\"")); break;
            case "duplicate": payload["future"] = JsonNode.Parse("{\"x\":1,\"x\":2}"); break;
            case "request-limit": options = options with { MaximumRequestBytes = 1 }; break;
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(CompactionFixture.Json())); using var source = new CodexFixtureSource(); var factory = new OpenAiHttpFactory(handler); var provider = Provider(factory, source, options);
        await Assert.ThrowsAnyAsync<Exception>(() => Invoke(provider, compact, fault == "cancel" ? new CancellationToken(true) : TestContext.Current.CancellationToken, payload, new(target, credential, "public-route", "session")));
        Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>一次来源结果不允许换主体/账户、配置变化、过期或取消，且不重取令牌。</summary>
    [Theory] [InlineData("principal")] [InlineData("account")] [InlineData("expired")] [InlineData("source")] [InlineData("cancel")]
    public async Task InvalidSource_DoesNotDispatchOrRefresh(string fault)
    {
        using var cancellation = new CancellationTokenSource(); CodexFixtureSource? source = null;
        source = new() { Handler = (principal, _) => { if (fault == "source") source!.BindingIdentity = "changed"; if (fault == "cancel") cancellation.Cancel(); return ValueTask.FromResult(new CodexAccessToken(fault == "principal" ? "other" : principal, fault == "account" ? "other" : Part("account_id").GetString()!, Token, fault == "expired" ? Now : Now.AddHours(1))); } };
        using (source)
        using (var handler = new StubHttpHandler((_, _) => Task.FromResult(CompactionFixture.Json())))
        {
            var factory = new OpenAiHttpFactory(handler); await Assert.ThrowsAnyAsync<Exception>(() => Invoke(Provider(factory, source), false, cancellation.Token)); Assert.Equal(1, source.Calls); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
        }
    }
    /// <summary>工厂不能引入未选认证或未绑定原生头，来源在发送前过期/变更也不发包。</summary>
    [Theory] [InlineData("api-key")] [InlineData("x-api-key")] [InlineData("beta")] [InlineData("originator")] [InlineData("source")] [InlineData("expired")]
    public async Task FactoryBoundary_RejectsUnboundDefaults(string fault)
    {
        using var source = new CodexFixtureSource(); var clock = new CodexTestClock(); var options = Options() with { Account = Options().Account with { Beta = null, Originator = null } };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(CompactionFixture.Json())); var factory = new OpenAiHttpFactory(handler) { Configure = c => { if (fault == "source") source.BindingIdentity = "changed"; else if (fault == "expired") clock.Current = Now.AddHours(1); else c.DefaultRequestHeaders.Add(fault == "beta" ? "OpenAI-Beta" : fault == "originator" ? "originator" : fault, "unbound"); } };
        await Assert.ThrowsAnyAsync<Exception>(() => Invoke(Provider(factory, source, options, clock), false, TestContext.Current.CancellationToken)); Assert.Equal(1, source.Calls); Assert.Equal(0, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>省略 Beta/originator 保持省略，不模拟 CLI；账户和 Bearer 仍显式选中。</summary>
    [Fact]
    public async Task OptionalHeaders_AreNotInvented()
    {
        using var handler = new StubHttpHandler((message, _) => { Assert.False(message.Headers.Contains("OpenAI-Beta")); Assert.False(message.Headers.Contains("originator")); Assert.Equal(Token, message.Headers.Authorization!.Parameter); return Task.FromResult(CompactionFixture.Json(Part("response").GetRawText())); });
        using var source = new CodexFixtureSource(); await Invoke(Provider(new OpenAiHttpFactory(handler), source, Options() with { Account = Options().Account with { Beta = null, Originator = null } }), false, TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>实际方法、服务器、账户、凭据、媒体或流选择不能在处理器中被替换。</summary>
    [Theory] [InlineData("method")] [InlineData("uri")] [InlineData("redirect")] [InlineData("account")] [InlineData("bearer")] [InlineData("content-type")] [InlineData("accept")]
    public async Task ActualWireMutation_RejectsBeforeSuccess(string fault)
    {
        using var handler = new StubHttpHandler((request, _) =>
        {
            var response = CompactionFixture.Json(Part("response").GetRawText());
            if (fault == "method") request.Method = HttpMethod.Get; if (fault == "uri") request.RequestUri = new("https://other.invalid/responses"); if (fault == "redirect") response.RequestMessage = new(HttpMethod.Post, "https://other.invalid/responses");
            if (fault == "account") { request.Headers.Remove("chatgpt-account-id"); request.Headers.Add("chatgpt-account-id", "other"); }
            if (fault == "bearer") request.Headers.Authorization = new("Bearer", "other"); if (fault == "content-type") request.Content!.Headers.ContentType!.CharSet = "utf-8"; if (fault == "accept") request.Headers.Accept.Add(new("text/html")); return Task.FromResult(response);
        }); using var source = new CodexFixtureSource(); var factory = new OpenAiHttpFactory(handler);
        await Assert.ThrowsAnyAsync<Exception>(() => Invoke(Provider(factory, source), false, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>请求和策略初始化不接收空白/坏头或无效预算，令牌默认显示与序列化不泄密。</summary>
    [Theory] [InlineData("account")] [InlineData("beta")] [InlineData("originator")] [InlineData("request")] [InlineData("response")] [InlineData("tail")]
    public void InvalidOptions_RejectBeforeCalls(string fault)
    {
        var options = Options(); options = fault switch { "account" => options with { Account = options.Account with { AccountId = "bad account" } }, "beta" => options with { Account = options.Account with { Beta = new string((char)13, 1) } }, "originator" => options with { Account = options.Account with { Originator = new string((char)0xd800, 1) } }, "request" => options with { MaximumRequestBytes = 0 }, "response" => options with { MaximumResponseBytes = 0 }, _ => options with { MaximumBufferedTerminalEvents = 0 } };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(CompactionFixture.Json())); using var source = new CodexFixtureSource(); var factory = new OpenAiHttpFactory(handler); Assert.ThrowsAny<Exception>(() => Provider(factory, source, options)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
}
