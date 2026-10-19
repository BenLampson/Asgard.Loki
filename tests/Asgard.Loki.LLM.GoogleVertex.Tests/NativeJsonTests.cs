using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>完整 Vertex 原生报文、资源路径和认证由独立金样验证，不用转换器生成预期。</summary>
public sealed class NativeJsonTests
{
    /// <summary>两种显式资源形式使用相同原生协议，不按模型名称猜测路径或能力。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task CompleteWireAndResult_MatchIndependentFixtures(bool endpoint, bool quota)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var resource = endpoint ? "projects/project-a/locations/global/endpoints/endpoint-123" : VertexFixture.Resource;
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("https://first.invalid/proxy/v1/" + resource + ":generateContent", request.RequestUri!.AbsoluteUri); Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
            Assert.Equal("Bearer fixture-token-1", request.Headers.Authorization!.ToString()); Assert.False(request.Headers.Contains("x-goog-api-key")); Assert.False(request.Headers.Contains("api-key"));
            if (quota) Assert.Equal("quota-project", Assert.Single(request.Headers.GetValues("x-goog-user-project"))); else Assert.False(request.Headers.Contains("x-goog-user-project"));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(VertexFixture.Text("wire")), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return VertexFixture.Reply();
        });
        var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexLlmProvider(factory, source, new() { QuotaProjectId = quota ? "quota-project" : null }, timeProvider: clock);
        await using (var client = VertexFixture.Client(provider, targets: [VertexFixture.Target(resource: resource)]))
        {
            var input = VertexFixture.Request(); var original = input.ToJsonString(); var result = await client.LLM.Gemini.GenerateAsync(input.Deserialize<GeminiContentRequest>()!, new() { SessionId = "vertex-session" }, TestContext.Current.CancellationToken);
            Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(VertexFixture.Text("response")), result.Data)); Assert.Equal(resource, result.Metadata.UpstreamModel); Assert.Equal("native-model-version", result.Data.GetProperty("modelVersion").GetString()); Assert.Equal(original, input.ToJsonString());
            Assert.Equal(9007199254740993L, result.Data.GetProperty("future").GetProperty("integer").GetInt64()); Assert.True(Assert.Single(factory.Clients).Disposed);
        }
        Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.Equal("Asgard.Loki.LLM.GoogleVertex", Assert.Single(factory.Names)); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
    /// <summary>直接调用也在等待认证源前拍摄快照，调用者修改不会进入报文。</summary>
    [Fact]
    public async Task InputSnapshot_PrecedesTokenAwait()
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Resolve = async (principal, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return new(principal, "fixture-token", clock.Now.AddHours(1)); };
        using var handler = new StubHttpHandler(async (request, token) => { Assert.True(JsonNode.DeepEquals(JsonNode.Parse(VertexFixture.Text("wire")), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return VertexFixture.Reply(); });
        var input = VertexFixture.Request(); var pending = new GoogleVertexLlmProvider(new VertexHttpFactory(handler), source, timeProvider: clock).GenerateAsync(input, VertexFixture.Context(), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken); input["contents"] = new JsonArray(); release.SetResult(); await pending; Assert.Equal(1, source.Calls);
    }
    /// <summary>身份令牌不进入默认 JSON/显示，原始材料只能由显式 getter 读取。</summary>
    [Fact]
    public void TokenFormatting_DoesNotExposePrincipalOrSecret()
    {
        var value = new GoogleVertexLlmAccessToken("private-principal", "private-token", new VertexClock().Now.AddHours(1)); var json = JsonSerializer.Serialize(value);
        Assert.DoesNotContain("private", json); Assert.Equal(nameof(GoogleVertexLlmAccessToken), value.ToString());
    }
}
