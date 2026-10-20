using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.CodexFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>通过真实 SDK 模块验证完整服务器/模型/外部主体/账户/实现绑定，缓存仍由显式协调器保护。</summary>
public sealed class CodexSessionTests
{
    /// <summary>账户、来源、原生头、策略、命名客户端、目标或主体变化均不能沿用已有 Session。</summary>
    [Theory] [InlineData("account")] [InlineData("source")] [InlineData("beta")] [InlineData("originator")] [InlineData("request")] [InlineData("response")] [InlineData("tail")] [InlineData("client")] [InlineData("server")] [InlineData("model")] [InlineData("principal")]
    public async Task ChangedCompleteBinding_FailsBeforeSecondSourceAndHttp(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(CompactionFixture.Json(Part("response").GetRawText()))); using var firstSource = new CodexFixtureSource(); var store = CompactionFixture.Store(); var factory = new OpenAiHttpFactory(handler);
        await using (var first = Client(Provider(factory, firstSource), store)) await first.LLM.Responses.CreateAsync(new() { Model = "public-route" }, CompactionFixture.Call("codex-session"), TestContext.Current.CancellationToken);
        var options = Options(); var target = Target(); var credential = Credential(); using var source = new CodexFixtureSource();
        options = change switch { "account" => options with { Account = options.Account with { AccountId = "other" } }, "beta" => options with { Account = options.Account with { Beta = "other" } }, "originator" => options with { Account = options.Account with { Originator = "other" } }, "request" => options with { MaximumRequestBytes = 100 }, "response" => options with { MaximumResponseBytes = 100 }, "tail" => options with { MaximumBufferedTerminalEvents = 2 }, _ => options };
        if (change == "source") source.BindingIdentity = "other"; if (change == "server") target = target with { BaseAddress = new("https://other.invalid/backend-api/codex/") }; if (change == "model") target = target with { UpstreamModel = "other" }; if (change == "principal") credential = LlmCredential.External("external-a", "other");
        await using var second = Client(Provider(factory, source, options, clientName: change == "client" ? "other" : "codex-test"), store, target, credential);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.Responses.CreateAsync(new() { Model = "public-route" }, CompactionFixture.Call("codex-session"), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Equal(1, handler.Calls); Assert.Single(factory.Clients);
    }
    /// <summary>同配置跨实例恢复响应归属，短期 Bearer 可轮换，不进入稳定 Session 指纹或元数据。</summary>
    [Fact]
    public async Task StablePrincipal_RotatesTokensAndRecoversResponseOwnership()
    {
        var requestCount = 0; using var handler = new StubHttpHandler(async (message, token) =>
        {
            requestCount++; Assert.Equal(requestCount == 1 ? "rotated-one" : "rotated-two", message.Headers.Authorization!.Parameter); var data = JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token))!.AsObject(); Assert.Equal("actual-model", data["model"]!.GetValue<string>()); if (requestCount == 2) Assert.Equal("resp-codex-a", data["previous_response_id"]!.GetValue<string>());
            return CompactionFixture.Json(Part("response").GetRawText());
        }); var factory = new OpenAiHttpFactory(handler); var store = CompactionFixture.Store();
        using var firstSource = new CodexFixtureSource { Handler = (principal, _) => ValueTask.FromResult(new CodexAccessToken(principal, Part("account_id").GetString()!, "rotated-one", Now.AddHours(1))) };
        using var secondSource = new CodexFixtureSource { Handler = (principal, _) => ValueTask.FromResult(new CodexAccessToken(principal, Part("account_id").GetString()!, "rotated-two", Now.AddHours(1))) };
        var firstProvider = Provider(factory, firstSource); var secondProvider = Provider(factory, secondSource); Assert.Equal(firstProvider.BindingIdentity, secondProvider.BindingIdentity);
        await using (var first = Client(firstProvider, store)) await first.LLM.Responses.CreateAsync(new() { Model = "public-route" }, CompactionFixture.Call("codex-session"), TestContext.Current.CancellationToken);
        await using (var second = Client(secondProvider, store)) { var result = await second.LLM.Responses.CreateAsync(new() { Model = "public-route", PreviousResponseId = "resp-codex-a" }, CompactionFixture.Call("codex-session"), TestContext.Current.CancellationToken); Assert.Equal("actual-model", result.Metadata.UpstreamModel); Assert.Null(result.Metadata.Conversion); }
        var binding = await store.GetAsync("codex-session", TestContext.Current.CancellationToken); Assert.NotNull(binding); Assert.Equal("codex-a", binding.TargetId); Assert.DoesNotContain("rotated-one", JsonSerializer.Serialize(binding)); Assert.DoesNotContain("rotated-two", JsonSerializer.Serialize(binding)); Assert.False(firstSource.Disposed); Assert.False(secondSource.Disposed); Assert.Equal(2, handler.Calls);
    }
}
