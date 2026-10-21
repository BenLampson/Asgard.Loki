using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using System.Text.Json.Nodes;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.CodexFixture;
using static Asgard.Loki.LLM.OpenAI.Tests.CodexSearchTests;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>原生辅助搜索独立验证请求快照、账户/来源、预算和最终资源边界。</summary>
public sealed class CodexSearchBoundaryTests
{
    /// <summary>错误目标/身份/策略在构造或调用前失败，不从环境和其他能力借用配置。</summary>
    [Theory] [InlineData("root")] [InlineData("userinfo")] [InlineData("apikey")] [InlineData("principal")] [InlineData("account")] [InlineData("request-budget")] [InlineData("response-budget")]
    public void InvalidConfiguration_RejectsBeforeSource(string fault)
    {
        var options = SearchOptions(); var credential = Credential(); options = fault switch { "root" => options with { BaseAddress = new("https://backend.invalid/root?query=1") }, "userinfo" => options with { BaseAddress = new("https://user:pass@backend.invalid/") }, "account" => options with { Account = options.Account with { AccountId = "bad account" } }, "request-budget" => options with { MaximumRequestBytes = 0 }, "response-budget" => options with { MaximumResponseBytes = 0 }, _ => options };
        if (fault == "apikey") credential = new("external-a", "not-external"); if (fault == "principal") credential = LlmCredential.External("external-a", new string((char)0xd800, 1));
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(CompactionFixture.Json())); using var source = new CodexFixtureSource(); var factory = new OpenAiHttpFactory(handler); Assert.ThrowsAny<Exception>(() => new CodexSearchClient(factory, options, credential, source, new CodexTestClock())); Assert.Empty(factory.Clients); Assert.Equal(0, source.Calls);
    }
    /// <summary>坏原始参数、预算和预取消在任何来源等待/网络之前失败。</summary>
    [Theory] [InlineData("unicode")] [InlineData("duplicate")] [InlineData("budget")] [InlineData("cancel")]
    public async Task InvalidRequest_RejectsBeforeToken(string fault)
    {
        var parameters = new JsonObject(); if (fault == "unicode") parameters["future"] = new string((char)0xd800, 1); if (fault == "duplicate") parameters["future"] = JsonNode.Parse("{\"x\":1,\"x\":2}");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(CompactionFixture.Json())); using var source = new CodexFixtureSource(); var factory = new OpenAiHttpFactory(handler); var options = fault == "budget" ? SearchOptions() with { MaximumRequestBytes = 1 } : SearchOptions();
        await Assert.ThrowsAnyAsync<Exception>(() => new CodexSearchClient(factory, options, Credential(), source, new CodexTestClock()).SearchAsync(parameters, fault == "cancel" ? new CancellationToken(true) : TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
    /// <summary>来源的主体、账户、有效期和稳定配置断言不可改变，不自动再取令牌。</summary>
    [Theory] [InlineData("principal")] [InlineData("account")] [InlineData("expiry")] [InlineData("source")]
    public async Task InvalidAuthentication_DoesNotDispatch(string fault)
    {
        CodexFixtureSource? source = null; source = new() { Handler = (principal, _) => { if (fault == "source") source!.BindingIdentity = "changed"; return ValueTask.FromResult(new CodexAccessToken(fault == "principal" ? "other" : principal, fault == "account" ? "other" : Part("account_id").GetString()!, Token, fault == "expiry" ? Now : Now.AddHours(1))); } };
        using (source) using (var handler = new StubHttpHandler((_, _) => Task.FromResult(CompactionFixture.Json())))
        { var factory = new OpenAiHttpFactory(handler); await Assert.ThrowsAnyAsync<Exception>(() => new CodexSearchClient(factory, SearchOptions(), Credential(), source, new CodexTestClock()).SearchAsync(new(), TestContext.Current.CancellationToken)); Assert.Equal(1, source.Calls); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls); }
    }
    /// <summary>公共读取边界由 Infrastructure.Tests 覆盖；此处保留预算接线、外层关闭及账户/目标复查。</summary>
    [Theory] [InlineData("client")] [InlineData("cancel-client")] [InlineData("source-body")] [InlineData("expired")] [InlineData("late-uri")] [InlineData("method")] [InlineData("redirect")] [InlineData("bytes")] [InlineData("unbound-auth")]
    public async Task TransportAndClosureFaults_PreventSuccessfulResult(string fault)
    {
        using var cancellation = new CancellationTokenSource(); using var source = new CodexFixtureSource(); var clock = new CodexTestClock(); var bytes = Bytes(Part("search_response").GetRawText());
        var body = new OllamaBodyStream(bytes) { OnEof = () => { if (fault == "expired") clock.Current = Now.AddHours(1); }, OnDispose = () => { if (fault == "source-body") source.BindingIdentity = "changed"; } };
        HttpRequestMessage? actual = null; using var handler = new StubHttpHandler((message, _) =>
        {
            actual = message; if (fault == "method") message.Method = HttpMethod.Get; var response = OpenAiHttpFixture.Reply(body, false);
            if (fault == "redirect") response.RequestMessage = new(HttpMethod.Post, "https://other.invalid/search"); return Task.FromResult(response);
        });
        var factory = new OpenAiHttpFactory(handler) { Configure = c => { if (fault == "unbound-auth") c.DefaultRequestHeaders.Add("api-key", "other"); }, OnDispose = () => { if (fault == "client") throw new IOException("client close"); if (fault == "cancel-client") cancellation.Cancel(); if (fault == "late-uri") actual!.RequestUri = new("https://other.invalid/search"); } };
        var options = fault == "bytes" ? SearchOptions() with { MaximumResponseBytes = bytes.Length - 1 } : SearchOptions();
        await Assert.ThrowsAnyAsync<Exception>(() => new CodexSearchClient(factory, options, Credential(), source, clock).SearchAsync(new(), cancellation.Token)); Assert.Equal(1, source.Calls); Assert.Equal(fault == "unbound-auth" ? 0 : 1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed); if (handler.Calls != 0) Assert.True(body.Disposed);
    }
}
