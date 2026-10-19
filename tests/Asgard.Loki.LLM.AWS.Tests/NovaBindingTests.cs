using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>新原生协议保持完整目标绑定，不能在已绑定 Session 中变成 Converse 或 Claude。</summary>
public sealed class NovaBindingTests
{
    /// <summary>同一供应商支持多种协议也不允许 Session 改协议。</summary>
    [Fact]
    public async Task ExistingSession_CannotSwitchToConverse()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(MantleFixture.Reply(new MemoryStream(MantleFixture.Bytes(NovaFixture.Golden().GetProperty("response").GetRawText())), false))); var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" });
        await using var client = NovaFixture.Client(provider, true, targets: [NovaFixture.Target() with { Protocols = LlmProtocol.BedrockNova | LlmProtocol.BedrockConverse }]);
        await client.LLM.BedrockNova.InvokeAsync(NovaFixture.Typed(), new() { SessionId = "aws-session" }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LLM.BedrockConverse.ConverseAsync(new() { Model = "public" }, new() { SessionId = "aws-session" }, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls); Assert.Single(factory.Clients);
    }
    /// <summary>重建客户端及调度器变更后，原生 JSON 仍用原服务器、实际模型和凭据身份。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RecreatedClient_RestoresCompleteNovaTarget(bool apiKey)
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("second.invalid", message.RequestUri!.Host); Assert.Contains("nova-selected", message.RequestUri.AbsolutePath); Assert.DoesNotContain("model", System.Text.Json.Nodes.JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!.AsObject().Select(p => p.Key));
            if (apiKey) Assert.Equal("fixture-nova-key", message.Headers.Authorization!.Parameter); else Assert.Contains("/bedrock/aws4_request", Assert.Single(message.Headers.GetValues("Authorization")));
            return MantleFixture.Reply(new MemoryStream(MantleFixture.Bytes(NovaFixture.Golden().GetProperty("response").GetRawText())), false);
        });
        var factory = new AwsHttpFactory(handler); var options = new AwsLlmOptions { Region = "us-east-1" }; AwsLlmProvider Provider() => apiKey ? new(factory, options) : new(factory, source, options, timeProvider: clock);
        var store = AwsFixture.Store(); var targets = new[] { NovaFixture.Target(), NovaFixture.Target("nova-selected") with { Id = "second", BaseAddress = new("https://second.invalid/runtime/") } };
        await using (var first = NovaFixture.Client(Provider(), apiKey, store, targets, 1)) await first.LLM.BedrockNova.InvokeAsync(NovaFixture.Typed(), new() { SessionId = "aws-session" }, TestContext.Current.CancellationToken);
        await using var second = NovaFixture.Client(Provider(), apiKey, store, targets, 0); var result = await second.LLM.BedrockNova.InvokeAsync(NovaFixture.Typed(), new() { SessionId = "aws-session" }, TestContext.Current.CancellationToken);
        Assert.Equal("second", result.Metadata.TargetId); Assert.Equal("nova-selected", result.Metadata.UpstreamModel); Assert.Equal(2, handler.Calls); Assert.Equal(apiKey ? 0 : 2, source.Calls);
    }
}
