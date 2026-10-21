using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>原生 Claude JSON 与 Gemini 保持各自结构，通过公开 SDK 检查完整路径、认证和样本。</summary>
public sealed class ClaudeJsonTests
{
    /// <summary>默认/显式版本以及可选 quota project 保留原生报文和实际返回模型。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task CompleteClaudeWireAndResult_MatchIndependentGoldens(bool explicitVersion, bool quota)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("https://first.invalid/proxy/v1/" + ClaudeFixture.Resource.Replace("@", "%40") + ":rawPredict", request.RequestUri!.AbsoluteUri); Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
            Assert.Equal("Bearer fixture-token-1", request.Headers.Authorization!.ToString()); Assert.False(request.Headers.Contains("x-api-key")); Assert.False(request.Headers.Contains("anthropic-version"));
            if (quota) Assert.Equal("quota-project", Assert.Single(request.Headers.GetValues("x-goog-user-project"))); else Assert.False(request.Headers.Contains("x-goog-user-project"));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(VertexFixture.Text("claude-wire")), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return ClaudeFixture.Reply();
        }); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexLlmProvider(factory, source, new() { ClaudeAnthropicVersion = explicitVersion ? "vertex-2023-10-16" : null, QuotaProjectId = quota ? "quota-project" : null }, timeProvider: clock);
        await using (var client = ClaudeFixture.Client(provider))
        {
            var input = ClaudeFixture.Request(); var original = input.ToJsonString(); var result = await client.LLM.ClaudeMessages.CreateAsync(input.Deserialize<ClaudeMessageRequest>()!, new() { SessionId = "vertex-claude-session" }, TestContext.Current.CancellationToken);
            Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(VertexFixture.Text("claude-response")), result.Data)); Assert.Equal(ClaudeFixture.Resource, result.Metadata.UpstreamModel); Assert.Equal("native-claude-version", result.Data.GetProperty("model").GetString()); Assert.Equal(original, input.ToJsonString()); Assert.True(Assert.Single(factory.Clients).Disposed);
        }
        Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>新增默认 Claude 操作不改变原 Gemini 的字节级供应商绑定身份。</summary>
    [Fact]
    public void DefaultOptions_PreservePrecedingGeminiBindingIdentity()
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var provider = new GoogleVertexLlmProvider(new VertexHttpFactory(handler), source);
        var legacyOptions = new { QuotaProjectId = (string?)null, MaximumBodyBytes = 32 * 1024 * 1024, MaximumBufferedTerminalEvents = 128, MaximumTrackedCandidates = 128 };
        var expected = provider.GetType().AssemblyQualifiedName + ":vertex-sse-v1:" + JsonSerializer.Serialize(new { Options = legacyOptions, Client = "Asgard.Loki.LLM.GoogleVertex", Source = "vertex-source-v1" }); Assert.Equal(expected, provider.BindingIdentity);
    }
    /// <summary>直接调用也在认证等待前快照正文，不让签名/工具内容被调用方修改。</summary>
    [Fact]
    public async Task ClaudeInputSnapshot_PrecedesTokenAwait()
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Resolve = async (principal, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return new(principal, "fixture-token", clock.Now.AddHours(1)); };
        using var handler = new StubHttpHandler(async (request, token) => { Assert.True(JsonNode.DeepEquals(JsonNode.Parse(VertexFixture.Text("claude-wire")), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return ClaudeFixture.Reply(); }); var factory = new VertexHttpFactory(handler); var input = ClaudeFixture.Request(); var pending = new GoogleVertexLlmProvider(factory, source, timeProvider: clock).CreateAsync(input, ClaudeFixture.Context(), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken); input["messages"] = new JsonArray(); release.SetResult(); await pending; Assert.Equal(1, source.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
}
