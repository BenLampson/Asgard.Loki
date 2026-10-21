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

/// <summary>固定 Express 上游的显式转换仍由转换包负责，不扩大原生供应商依赖或认证种类。</summary>
public sealed class ExpressConversionTests
{
    /// <summary>同一显式文本子集的完整/流式 Chat 调用使用 Express 路径与唯一所选 Key。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExplicitChatConversion_UsesExpressNativeTransport(bool stream)
    {
        const string reply = "{\"candidates\":[{\"index\":0,\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"done\"}]},\"finishReason\":\"STOP\"}]}";
        const string events = "data: {\"candidates\":[{\"index\":0,\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"done\"}]}}]}\n\ndata: {\"candidates\":[{\"index\":0,\"finishReason\":\"STOP\"}]}\n\n";
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("https://first.invalid/proxy/v1/" + ExpressFixture.Resource + (stream ? ":streamGenerateContent?alt=sse" : ":generateContent"), request.RequestUri!.AbsoluteUri); Assert.Equal(ExpressFixture.Key, Assert.Single(request.Headers.GetValues("x-goog-api-key"))); Assert.Null(request.Headers.Authorization); Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"contents":[{"role":"user","parts":[{"text":"hello"}]}]}"""), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return stream ? StreamingTests.Reply(new OllamaBodyStream(Encoding.UTF8.GetBytes(events))) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }); var factory = new VertexHttpFactory(handler); var native = new GoogleVertexExpressLlmProvider(factory); var converted = new ConvertingLlmProvider(native, LlmProtocol.Gemini, "express-converted"); await using var client = new LokiClientBuilder().AddLLM(new() { Providers = [converted], Credentials = [new("identity", ExpressFixture.Key)], SessionStore = VertexFixture.Store(), Routes = [new() { Model = "public", Targets = [ExpressFixture.Target() with { ProviderId = converted.Id, Protocols = LlmProtocol.ChatCompletions }] }] }).Build(); var request = new ChatCompletionRequest { Model = "public", Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }) };
        if (stream)
        {
            var observed = new List<ChatCompletionEvent>(); await foreach (var item in client.LLM.ChatCompletions.StreamAsync(request, new() { SessionId = "express-converted-session" }, TestContext.Current.CancellationToken)) observed.Add(item); Assert.Contains(observed, item => item.Data.GetProperty("choices").EnumerateArray().Any(choice => choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var text) && text.GetString() == "done")); Assert.Single(observed, item => item.Data.GetProperty("choices").EnumerateArray().Any(choice => choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() == "stop")); Assert.All(observed, item => { Assert.Empty(item.Metadata.Conversion!.Diagnostics); Assert.Equal(ExpressFixture.Resource, item.Metadata.UpstreamModel); });
        }
        else
        {
            var result = await client.LLM.ChatCompletions.CompleteAsync(request, new() { SessionId = "express-converted-session" }, TestContext.Current.CancellationToken); Assert.Equal("done", result.Data.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()); Assert.Empty(result.Metadata.Conversion!.Diagnostics); Assert.Equal(ExpressFixture.Resource, result.Metadata.UpstreamModel);
        }
        Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed);
    }
}
