using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Conversion;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>转换包仅测试时引用，固定 Claude 上游不改变 Bedrock 原生认证/模型。</summary>
public sealed class ConversionTests
{
    /// <summary>公共 Chat 文本显式转换到 Bedrock Claude，保持最终原生正文与已选 External 身份。</summary>
    [Fact]
    public async Task ExplicitChatViaClaude_UsesNativeBedrockWire()
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler(async (request, token) => { Assert.Equal("https://first.invalid/proxy/model/anthropic.configured-model-v1%3A0/invoke", request.RequestUri!.AbsoluteUri); Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/", Assert.Single(request.Headers.GetValues("Authorization"))); Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"messages":[{"role":"user","content":[{"type":"text","text":"hello"}]}],"max_tokens":512,"anthropic_version":"bedrock-2023-05-31"}"""), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"native-message","type":"message","role":"assistant","model":"native","content":[{"type":"text","text":"done"}],"stop_reason":"end_turn","usage":{"input_tokens":2,"output_tokens":1}}""", Encoding.UTF8, "application/json") }; }); var factory = new AwsHttpFactory(handler); var native = new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock); var converted = new ConvertingLlmProvider(native, LlmProtocol.ClaudeMessages, "explicit-aws", new() { DefaultMaxTokens = 512 }); await using var client = new LokiClientBuilder().AddLLM(new() { Providers = [converted], Credentials = [LlmCredential.External("identity", "principal-a")], SessionStore = AwsFixture.Store(), Routes = [new() { Model = "public", Targets = [AwsFixture.Target() with { ProviderId = converted.Id, Protocols = LlmProtocol.ChatCompletions }] }] }).Build(); var result = await client.LLM.ChatCompletions.CompleteAsync(new() { Model = "public", Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }) }, new() { SessionId = "converted-aws-session" }, TestContext.Current.CancellationToken); Assert.Equal("done", result.Data.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()); Assert.Empty(result.Metadata.Conversion!.Diagnostics); Assert.Equal(AwsFixture.Model, result.Metadata.UpstreamModel); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
}
