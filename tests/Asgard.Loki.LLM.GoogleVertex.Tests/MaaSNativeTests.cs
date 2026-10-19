using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>MaaS 以独立完整报文验证路径、字段/工具关联和原生结果保真。</summary>
public sealed class MaaSNativeTests
{
    /// <summary>显式区域/全局、版本和项目头均保留完整请求与结果，而不猜测主机或模型后缀。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task CompleteWireAndResult_MatchIndependentGoldens(bool global, bool quota)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(VertexFixture.Text("maas-response"))); var root = "https://first.invalid/proxy/" + (global ? "v1beta1/projects/project-a/locations/global/" : "v1/projects/project-a/locations/us-central1/") + "endpoints/openapi/";
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal(root + "chat/completions", request.RequestUri!.AbsoluteUri); Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("Bearer fixture-token-1", request.Headers.Authorization!.ToString()); Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType); Assert.False(request.Headers.Contains("x-goog-api-key"));
            if (quota) Assert.Equal("selected-quota", Assert.Single(request.Headers.GetValues("x-goog-user-project"))); else Assert.False(request.Headers.Contains("x-goog-user-project"));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(VertexFixture.Text("maas-wire")), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); var response = StreamingTests.Reply(body); response.Content.Headers.ContentType = new("application/json"); return response;
        }); var factory = new VertexHttpFactory(handler) { DefaultHeader = "Authorization" }; var provider = new GoogleVertexMaaSLlmProvider(factory, source, new() { QuotaProjectId = quota ? "selected-quota" : null }, timeProvider: clock); await using var client = MaaSFixture.Client(provider, targets: [MaaSFixture.Target() with { BaseAddress = new(root) }]);
        var result = await MaaSFixture.Invoke(client); Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(VertexFixture.Text("maas-response")), result.Data)); Assert.Equal(MaaSFixture.Model, result.Metadata.UpstreamModel); Assert.Equal("native-returned-model", result.Data.GetProperty("model").GetString()); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls); Assert.Equal("Asgard.Loki.LLM.GoogleVertex.MaaS", Assert.Single(factory.Names)); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
    /// <summary>原生 JSON 在第一次认证等待前复制，等待期间调用者修改不影响已选请求。</summary>
    [Fact]
    public async Task InputSnapshot_PrecedesTokenAwait()
    {
        var clock = new VertexClock(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var source = new VertexTokenSource(clock) { Resolve = async (principal, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return new(principal, "fixture-token", clock.Now.AddHours(1)); } };
        using var handler = new StubHttpHandler(async (request, token) => { Assert.True(JsonNode.DeepEquals(JsonNode.Parse(VertexFixture.Text("maas-wire")), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return MaaSFixture.Reply(); }); var input = MaaSFixture.Request(); var provider = new GoogleVertexMaaSLlmProvider(new VertexHttpFactory(handler), source, timeProvider: clock); var call = provider.CompleteAsync(input, MaaSFixture.Context(), TestContext.Current.CancellationToken); await entered.Task.WaitAsync(TestContext.Current.CancellationToken); input["messages"] = new JsonArray(); release.SetResult(); await call; Assert.Equal(1, handler.Calls);
    }
    /// <summary>未交付的流式操作在取令牌/HTTP 之前明确失败，不模拟单事件流。</summary>
    [Fact]
    public void Sse_IsExplicitUnsupportedBoundary()
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexMaaSLlmProvider(factory, source, timeProvider: clock); Assert.Throws<NotSupportedException>(() => provider.StreamAsync(MaaSFixture.Request(), MaaSFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
}
