using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Conversion;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>显式转换只在原生二进制来源完整结束后完成，不引用转换包到生产供应商。</summary>
public sealed class StreamConversionTests
{
    /// <summary>公共 Chat 通过固定 Claude 上游转换，来源 EOF 故障不发出成功 finish。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExplicitChatStream_RespectsNativeBinaryTermination(bool lateFailure)
    {
        var wire = new[] { """{"type":"message_start","message":{"id":"native-message","type":"message","role":"assistant","model":"native-model","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":2,"output_tokens":0}}}""", "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"done\"}}", "{\"type\":\"content_block_stop\",\"index\":0}", StreamFixture.Delta, StreamFixture.Stop }.SelectMany(StreamFixture.Chunk).ToArray(); var body = new OllamaBodyStream(wire) { FailAtEof = lateFailure }; var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler(async (request, token) => { Assert.Equal("https://first.invalid/proxy/model/anthropic.configured-model-v1%3A0/invoke-with-response-stream", request.RequestUri!.AbsoluteUri); Assert.Equal("application/json", Assert.Single(request.Headers.GetValues("x-amzn-bedrock-accept"))); Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"messages":[{"role":"user","content":[{"type":"text","text":"hello"}]}],"max_tokens":512,"anthropic_version":"bedrock-2023-05-31"}"""), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return StreamFixture.Reply(body); }); var factory = new AwsHttpFactory(handler); var native = new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock); var converted = new ConvertingLlmProvider(native, LlmProtocol.ClaudeMessages, "aws-converted", new() { DefaultMaxTokens = 512 }); await using var client = new LokiClientBuilder().AddLLM(new() { Providers = [converted], Credentials = [LlmCredential.External("identity", "principal-a")], SessionStore = AwsFixture.Store(), Routes = [new() { Model = "public", Targets = [AwsFixture.Target() with { ProviderId = converted.Id, Protocols = LlmProtocol.ChatCompletions }] }] }).Build(); var observed = new List<ChatCompletionEvent>();
        static bool Finished(ChatCompletionEvent item) => item.Data.GetProperty("choices").EnumerateArray().Any(choice => choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() == "stop");
        async Task Consume() { await foreach (var item in client.LLM.ChatCompletions.StreamAsync(new() { Model = "public", Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }) }, new() { SessionId = "aws-converted-session" }, TestContext.Current.CancellationToken)) { if (Finished(item)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } observed.Add(item); } }
        if (lateFailure) { await Assert.ThrowsAsync<IOException>(Consume); Assert.DoesNotContain(observed, Finished); } else { await Consume(); Assert.Single(observed, Finished); Assert.Contains(observed, item => item.Data.GetProperty("choices").EnumerateArray().Any(choice => choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var text) && text.GetString() == "done")); } Assert.All(observed, item => { Assert.Empty(item.Metadata.Conversion!.Diagnostics); Assert.Equal(AwsFixture.Model, item.Metadata.UpstreamModel); Assert.Null(item.GetAwsEventStreamFrame()); }); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
}
