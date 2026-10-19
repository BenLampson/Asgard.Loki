using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>Claude 复用稳定主体/完整资源绑定，不自动改版本、换账号或按模型名称切协议。</summary>
public sealed class ClaudeBindingTests
{
    /// <summary>正文版本存在时必须与配置一致；null/类型/值冲突都在认证之前失败。</summary>
    [Theory] [InlineData("other")] [InlineData("null")] [InlineData("number")]
    public async Task ConflictingBodyVersion_IsRejectedBeforeToken(string supplied)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(ClaudeFixture.Reply())); var factory = new VertexHttpFactory(handler); var input = ClaudeFixture.Request(); input["anthropic_version"] = supplied == "null" ? null : supplied == "number" ? JsonValue.Create(1) : JsonValue.Create("different-version");
        await Assert.ThrowsAsync<ArgumentException>(() => new GoogleVertexLlmProvider(factory, source, timeProvider: clock).CreateAsync(input, ClaudeFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>版本由调用者显式选择，SDK 不把自定义版本降回默认。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MatchingCustomVersion_IsPreserved(bool supplied)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler(async (request, token) =>
        {
            var expected = JsonNode.Parse(VertexFixture.Text("claude-wire"))!.AsObject(); expected["anthropic_version"] = "vertex-explicit-version"; Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return ClaudeFixture.Reply();
        }); var input = ClaudeFixture.Request(); if (supplied) input["anthropic_version"] = "vertex-explicit-version";
        await new GoogleVertexLlmProvider(new VertexHttpFactory(handler), source, new() { ClaudeAnthropicVersion = "vertex-explicit-version" }, timeProvider: clock).CreateAsync(input, ClaudeFixture.Context(), TestContext.Current.CancellationToken); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    /// <summary>错误资源形式或 publisher 不能借用 Claude rawPredict 去调用其他协议。</summary>
    [Theory] [InlineData(VertexFixture.Resource)] [InlineData("projects/project-a/locations/global/endpoints/endpoint-123")]
    public async Task NonAnthropicResource_IsRejectedBeforeAuthentication(string resource)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(ClaudeFixture.Reply())); var factory = new VertexHttpFactory(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new GoogleVertexLlmProvider(factory, source, timeProvider: clock).CreateAsync(ClaudeFixture.Request(), new(ClaudeFixture.Target(resource: resource), LlmCredential.External("identity", "principal-a"), "public", "session"), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
    /// <summary>来自第一方 Anthropic 的默认 Key/版本头不能混入 Vertex 的所选认证。</summary>
    [Theory] [InlineData("x-api-key")] [InlineData("anthropic-version")]
    public async Task FirstPartyDefaults_AreRejectedBeforeSend(string header)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(ClaudeFixture.Reply())); var factory = new VertexHttpFactory(handler) { DefaultHeader = header };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GoogleVertexLlmProvider(factory, source, timeProvider: clock).CreateAsync(ClaudeFixture.Request(), ClaudeFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls); Assert.Equal(1, source.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>令牌轮换和新客户端仍恢复原服务器、项目、区域与实际 Claude 资源。</summary>
    [Fact]
    public async Task ClaudeSession_SurvivesTokenRotationWithoutReselection()
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var store = VertexFixture.Store(); var resource = ClaudeFixture.Resource.Replace("project-a", "project-b"); using var handler = new StubHttpHandler((request, _) => { Assert.Equal("second.invalid", request.RequestUri!.Host); Assert.Contains("projects/project-b/", request.RequestUri.AbsolutePath); Assert.Equal("Bearer fixture-token-" + source.Calls, request.Headers.Authorization!.ToString()); return Task.FromResult(ClaudeFixture.Reply()); }); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexLlmProvider(factory, source, timeProvider: clock); var targets = new[] { ClaudeFixture.Target(), ClaudeFixture.Target("second", resource) };
        await using (var first = ClaudeFixture.Client(provider, store, targets, 1)) await ClaudeFixture.Invoke(first);
        await using (var next = ClaudeFixture.Client(provider, store, targets)) await ClaudeFixture.Invoke(next);
        Assert.Equal(2, source.Calls); Assert.Equal(2, handler.Calls); Assert.All(factory.Clients, client => Assert.True(client.Disposed)); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
    /// <summary>主体、版本或协议改变必须拒绝旧 Session，不能以另一个操作恢复。</summary>
    [Theory] [InlineData("principal")] [InlineData("version")] [InlineData("protocol")]
    public async Task ChangedClaudeBinding_IsRejectedBeforeToken(string change)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var store = VertexFixture.Store(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(ClaudeFixture.Reply())); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexLlmProvider(factory, source, timeProvider: clock);
        await using (var first = ClaudeFixture.Client(provider, store)) await ClaudeFixture.Invoke(first);
        if (change == "version") provider = new(factory, source, new() { ClaudeAnthropicVersion = "different-version" }, timeProvider: clock);
        if (change == "protocol") { await using var next = VertexFixture.Client(provider, store); await Assert.ThrowsAsync<InvalidOperationException>(() => next.LLM.Gemini.GenerateAsync(VertexFixture.Request().Deserialize<GeminiContentRequest>()!, new() { SessionId = "vertex-claude-session" }, TestContext.Current.CancellationToken)); }
        else { await using var next = ClaudeFixture.Client(provider, store, principal: change == "principal" ? "principal-b" : "principal-a"); await Assert.ThrowsAsync<InvalidOperationException>(() => ClaudeFixture.Invoke(next)); }
        Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    /// <summary>缺省之外的版本配置仍要求非空、可编码值。</summary>
    [Theory] [InlineData("")] [InlineData(" ")]
    public void InvalidVersionOption_IsRejected(string version)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); Assert.Throws<ArgumentException>(() => new GoogleVertexLlmProvider(new VertexHttpFactory(handler), new VertexTokenSource(new()), new() { ClaudeAnthropicVersion = version }));
    }
}
