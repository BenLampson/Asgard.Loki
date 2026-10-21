using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Conversion;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>显式转换接入使用固定 Vertex 原生上游；生产供应商不引用转换包。</summary>
public sealed class ConversionTests
{
    /// <summary>公共 Chat 文本转换到原生 Gemini 请求，完整目标和 External 主体仍保持绑定。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExplicitChatConversion_UsesNativeVertexTransport(bool stream)
    {
        const string reply = """{"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"done"}]},"finishReason":"STOP"}]}"""; var clock = new VertexClock(); var source = new VertexTokenSource(clock);
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("https://first.invalid/proxy/v1/" + VertexFixture.Resource + (stream ? ":streamGenerateContent?alt=sse" : ":generateContent"), request.RequestUri!.AbsoluteUri); Assert.Equal("Bearer fixture-token-1", request.Headers.Authorization!.ToString());
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"contents":[{"role":"user","parts":[{"text":"hello"}]}]}"""), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))));
            return stream ? StreamingTests.Reply(new OllamaBodyStream(Encoding.UTF8.GetBytes("data: {\"candidates\":[{\"index\":0,\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"done\"}]}}]}\n\ndata: {\"candidates\":[{\"index\":0,\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":2,\"candidatesTokenCount\":1,\"totalTokenCount\":3}}\n\n"))) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        });
        var native = new GoogleVertexLlmProvider(new VertexHttpFactory(handler), source, timeProvider: clock); var converted = new ConvertingLlmProvider(native, LlmProtocol.Gemini, "explicit");
        await using var client = new LokiClientBuilder().AddLLM(new() { Providers = [converted], Credentials = [LlmCredential.External("identity", "principal-a")], SessionStore = VertexFixture.Store(), Routes = [new() { Model = "public", Targets = [VertexFixture.Target() with { ProviderId = converted.Id, Protocols = LlmProtocol.ChatCompletions }] }] }).Build();
        var input = new ChatCompletionRequest { Model = "public", Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }) };
        if (stream)
        {
            var events = new List<ChatCompletionEvent>(); await foreach (var item in client.LLM.ChatCompletions.StreamAsync(input, new() { SessionId = "vertex-session" }, TestContext.Current.CancellationToken)) events.Add(item);
            Assert.Contains(events, item => item.Data.GetProperty("choices").EnumerateArray().Any(choice => choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var text) && text.GetString() == "done"));
            Assert.Single(events, item => item.Data.GetProperty("choices").EnumerateArray().Any(choice => choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() == "stop"));
            Assert.Equal(3, events[^1].Data.GetProperty("usage").GetProperty("total_tokens").GetInt32()); Assert.All(events, item => { Assert.Empty(item.Metadata.Conversion!.Diagnostics); Assert.Equal(VertexFixture.Resource, item.Metadata.UpstreamModel); });
        }
        else
        {
            var result = await client.LLM.ChatCompletions.CompleteAsync(input, new() { SessionId = "vertex-session" }, TestContext.Current.CancellationToken);
            Assert.Equal("done", result.Data.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()); Assert.Empty(result.Metadata.Conversion!.Diagnostics); Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(reply), result.Metadata.Conversion.UpstreamData)); Assert.Equal(VertexFixture.Resource, result.Metadata.UpstreamModel);
        }
        Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
}
