using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Conversion;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.AWS.Tests.MantleFixture;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>独立显式转换器委托 Mantle 的 External 认证，不给生产 AWS 包增加转换依赖。</summary>
public sealed class MantleConversionTests
{
    /// <summary>公共 Chat 高级设置显式转换后保持完整签名目标和原生凭据来源。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExplicitSettings_ReachSignedNativeMessages(bool stream)
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); const string result = "{\"id\":\"msg\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"native-model\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":2,\"output_tokens\":1}}";
        var start = Frame("{\"type\":\"message_start\",\"message\":{\"id\":\"msg\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"native-model\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":2,\"output_tokens\":0}}}");
        var text = Frame("{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}") + Frame("{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ok\"}}") + Frame("{\"type\":\"content_block_stop\",\"index\":0}");
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://bedrock-mantle.us-east-1.api.aws/anthropic/v1/messages", message.RequestUri!.AbsoluteUri); Assert.Contains("/bedrock-mantle/aws4_request", Assert.Single(message.Headers.GetValues("Authorization"))); Assert.False(message.Headers.Contains("x-api-key"));
            var body = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token)); var expected = JsonNode.Parse("{\"model\":\"anthropic.configured-model\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}],\"max_tokens\":2048,\"thinking\":{\"type\":\"adaptive\"},\"output_config\":{\"effort\":\"high\"}}")!.AsObject(); expected["stream"] = stream; Assert.True(JsonNode.DeepEquals(expected, body));
            return Reply(new MemoryStream(Bytes(stream ? start + text + Frame(Finish) + Frame(Stop) : result)), stream);
        });
        var native = new AwsMantleLlmProvider(new AwsHttpFactory(handler), source, new() { Region = "us-east-1" }, timeProvider: clock); var converted = new ConvertingLlmProvider(native, LlmProtocol.ClaudeMessages, "explicit"); await using var client = Client(converted, targets: [Target(converted.Id) with { Protocols = LlmProtocol.ChatCompletions }]);
        var request = JsonSerializer.Deserialize<ChatCompletionRequest>("{\"model\":\"public\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"max_completion_tokens\":2048,\"reasoning_effort\":\"high\"}")!;
        if (stream) { List<ChatCompletionEvent> seen = []; await foreach (var item in client.LLM.ChatCompletions.StreamAsync(request, new() { SessionId = "mantle-session" }, TestContext.Current.CancellationToken)) seen.Add(item); Assert.NotEmpty(seen); Assert.All(seen, item => Assert.Empty(item.Metadata.Conversion!.Diagnostics)); }
        else { var output = await client.LLM.ChatCompletions.CompleteAsync(request, new() { SessionId = "mantle-session" }, TestContext.Current.CancellationToken); Assert.Empty(output.Metadata.Conversion!.Diagnostics); Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(result), output.Metadata.Conversion.UpstreamData)); }
        Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    /// <summary>转换不能在 Mantle stop 之后的原生错误前释放目标成功。</summary>
    [Fact]
    public async Task LateNativeError_PreventsConvertedFinish()
    {
        const string error = "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\"}}";
        var clock = new AwsClock(); var start = Frame("{\"type\":\"message_start\",\"message\":{\"id\":\"msg\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"native-model\",\"stop_reason\":null}}");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(start + Frame(Finish) + Frame(Stop) + Frame(error))), true)));
        var source = new AwsCredentialSource(clock); var native = new AwsMantleLlmProvider(new AwsHttpFactory(handler), source, new() { Region = "us-east-1" }, timeProvider: clock); var converted = new ConvertingLlmProvider(native, LlmProtocol.ClaudeMessages, "explicit"); await using var client = Client(converted, targets: [Target(converted.Id) with { Protocols = LlmProtocol.ChatCompletions }]); List<ChatCompletionEvent> seen = [];
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { await foreach (var item in client.LLM.ChatCompletions.StreamAsync(new() { Model = "public", Messages = [new JsonObject { ["role"] = "user", ["content"] = "hi" }], Parameters = new() { ["max_completion_tokens"] = JsonSerializer.SerializeToElement(128) } }, new() { SessionId = "mantle-session" }, TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.Equal(HttpStatusCode.OK, failure.StatusCode); Assert.Equal(error, failure.ResponseBody); Assert.DoesNotContain(seen, item => item.Data.GetProperty("choices").EnumerateArray().Any(choice => choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind != JsonValueKind.Null)); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
}
