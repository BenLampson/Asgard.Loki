using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Conversion;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>显式转换固定到 Vertex Claude 原生上游，不因为资源模型名而自动选择协议。</summary>
public sealed class ClaudeConversionTests
{
    /// <summary>公共 Chat 文本按已有严格转换映射到 Claude rawPredict，认证和实际资源仍保持绑定。</summary>
    [Fact]
    public async Task ExplicitChatViaClaude_UsesNativeVertexWireAndAuthentication()
    {
        const string reply = """{"id":"msg-native","type":"message","role":"assistant","model":"native-claude","content":[{"type":"text","text":"done"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":2,"output_tokens":1}}"""; var clock = new VertexClock(); var source = new VertexTokenSource(clock);
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.EndsWith(":rawPredict", request.RequestUri!.AbsoluteUri); Assert.Equal("Bearer fixture-token-1", request.Headers.Authorization!.ToString());
            var expected = JsonNode.Parse("""{"messages":[{"role":"user","content":[{"type":"text","text":"hello"}]}],"max_tokens":512,"stream":false,"anthropic_version":"vertex-2023-10-16"}"""); Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }); var factory = new VertexHttpFactory(handler); var native = new GoogleVertexLlmProvider(factory, source, timeProvider: clock); var converted = new ConvertingLlmProvider(native, LlmProtocol.ClaudeMessages, "explicit-claude", new() { DefaultMaxTokens = 512 });
        await using var client = new LokiClientBuilder().AddLLM(new() { Providers = [converted], Credentials = [LlmCredential.External("identity", "principal-a")], SessionStore = VertexFixture.Store(), Routes = [new() { Model = "public", Targets = [ClaudeFixture.Target() with { ProviderId = converted.Id, Protocols = LlmProtocol.ChatCompletions }] }] }).Build();
        var result = await client.LLM.ChatCompletions.CompleteAsync(new() { Model = "public", Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }) }, new() { SessionId = "converted-claude-session" }, TestContext.Current.CancellationToken);
        Assert.Equal("done", result.Data.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()); Assert.Empty(result.Metadata.Conversion!.Diagnostics); Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(reply), result.Metadata.Conversion.UpstreamData)); Assert.Equal(ClaudeFixture.Resource, result.Metadata.UpstreamModel); Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
}
