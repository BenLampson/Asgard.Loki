using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Conversion;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>显式转换固定 MaaS Chat 上游，终态遵守原生传输完整性，不增加生产依赖。</summary>
public sealed class MaaSStreamConversionTests
{
    /// <summary>公共 Responses 文本转换只在原生来源正常结束后完成；迟读失败不产生 completed。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ResponsesViaNativeChat_UsesFixedTargetAndDelayedCompletion(bool lateFailure)
    {
        const string text = "data: {\"id\":\"native-chat\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"native\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"done\"}}]}\n\ndata: {\"id\":\"native-chat\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"native\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(text)) { FailAtEof = lateFailure }; using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal(MaaSFixture.Target().BaseAddress.AbsoluteUri + "chat/completions", request.RequestUri!.AbsoluteUri); Assert.Equal("Bearer fixture-token-1", request.Headers.Authorization!.ToString()); Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"model":"deepseek-ai/configured-model","stream":true,"messages":[{"role":"user","content":[{"type":"text","text":"hello"}]}]}"""), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return StreamingTests.Reply(body);
        }); var factory = new VertexHttpFactory(handler); var native = new GoogleVertexMaaSLlmProvider(factory, source, timeProvider: clock); var converted = new ConvertingLlmProvider(native, LlmProtocol.ChatCompletions, "explicit-maas"); await using var client = new LokiClientBuilder().AddLLM(new() { Providers = [converted], Credentials = [LlmCredential.External("identity", "principal-a")], SessionStore = VertexFixture.Store(), Routes = [new() { Model = "public", Targets = [MaaSFixture.Target() with { ProviderId = converted.Id, Protocols = LlmProtocol.Responses }] }] }).Build(); var observed = new List<ResponseEvent>();
        async Task Consume() { await foreach (var item in client.LLM.Responses.StreamAsync(new ResponseRequest { Model = "public", Input = JsonValue.Create("hello")! }, new() { SessionId = "converted-maas-session" }, TestContext.Current.CancellationToken)) { if (item.Data.GetProperty("type").GetString() == "response.completed") { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } observed.Add(item); } }
        if (lateFailure) { await Assert.ThrowsAsync<IOException>(Consume); Assert.DoesNotContain(observed, item => item.Data.GetProperty("type").GetString() == "response.completed"); }
        else { await Consume(); Assert.Single(observed, item => item.Data.GetProperty("type").GetString() == "response.completed"); Assert.Contains(observed, item => item.Data.GetProperty("type").GetString() == "response.output_text.delta" && item.Data.GetProperty("delta").GetString() == "done"); }
        Assert.All(observed, item => { Assert.Empty(item.Metadata.Conversion!.Diagnostics); Assert.Equal(MaaSFixture.Model, item.Metadata.UpstreamModel); }); Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
}
