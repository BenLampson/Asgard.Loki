using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Conversion;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>认证声明经显式转换委托给固定 AWS 上游，供应商生产包仍不引用 Conversion。</summary>
public sealed class ApiKeyConversionTests
{
    /// <summary>公共 Chat 的完整/流式常见文本子集保持最终请求及认证，晚期错误不能合成成功 finish。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task ExplicitChatConversion_UsesSelectedBearerAndNativeTermination(bool stream, bool lateFailure)
    {
        const string reply = """{"id":"native-message","type":"message","role":"assistant","model":"native-model","content":[{"type":"text","text":"done"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":2,"output_tokens":1}}""";
        var wire = new[] { """{"type":"message_start","message":{"id":"native-message","type":"message","role":"assistant","model":"native-model","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":2,"output_tokens":0}}}""", "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"done\"}}", "{\"type\":\"content_block_stop\",\"index\":0}", StreamFixture.Delta, StreamFixture.Stop }.SelectMany(StreamFixture.Chunk).ToArray(); var body = new OllamaBodyStream(stream ? wire : Encoding.UTF8.GetBytes(reply)) { FailAtEof = lateFailure };
        using var handler = new StubHttpHandler(async (request, token) => { Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal(ApiKeyFixture.Key, request.Headers.Authorization.Parameter); Assert.Equal("https://first.invalid/proxy/model/anthropic.configured-model-v1%3A0/" + (stream ? "invoke-with-response-stream" : "invoke"), request.RequestUri!.AbsoluteUri); Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"messages":[{"role":"user","content":[{"type":"text","text":"hello"}]}],"max_tokens":512,"anthropic_version":"bedrock-2023-05-31"}"""), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return ApiKeyFixture.Reply(stream, body); }); var factory = new AwsHttpFactory(handler); var native = new AwsLlmProvider(factory, new() { Region = "us-east-1" }); var converted = new ConvertingLlmProvider(native, LlmProtocol.ClaudeMessages, "aws-converted", new() { DefaultMaxTokens = 512 }); await using var client = new LokiClientBuilder().AddLLM(new() { Providers = [converted], Credentials = [new("identity", ApiKeyFixture.Key)], SessionStore = AwsFixture.Store(), Routes = [new() { Model = "public", Targets = [AwsFixture.Target() with { ProviderId = converted.Id, Protocols = LlmProtocol.ChatCompletions }] }] }).Build(); var input = new ChatCompletionRequest { Model = "public", Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }) }; var token = TestContext.Current.CancellationToken;
        if (!stream) { var result = await client.LLM.ChatCompletions.CompleteAsync(input, new() { SessionId = "key-conversion" }, token); Assert.Equal("done", result.Data.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()); Assert.Empty(result.Metadata.Conversion!.Diagnostics); }
        else
        {
            var finished = false; async Task Consume() { await foreach (var item in client.LLM.ChatCompletions.StreamAsync(input, new() { SessionId = "key-conversion" }, token)) { if (item.Data.GetProperty("choices")[0].GetProperty("finish_reason").ValueKind == JsonValueKind.String) { finished = true; Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } Assert.Empty(item.Metadata.Conversion!.Diagnostics); Assert.Null(item.GetAwsEventStreamFrame()); } } if (lateFailure) { await Assert.ThrowsAsync<IOException>(Consume); Assert.False(finished); } else { await Consume(); Assert.True(finished); }
        }
        Assert.Equal(1, handler.Calls); Assert.False(factory.Disposed);
    }
}
