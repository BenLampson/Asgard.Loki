using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>完整独立原生报文及实际签名，保留 Converse 媒体/工具/推理/格式而不改为 Claude。</summary>
public sealed class ConverseNativeTests
{
    /// <summary>普通模型/ARN 和静态/临时凭据均以实际原始正文签名，公共和适配器入口一致。</summary>
    [Theory] [InlineData(false, false, false)] [InlineData(false, true, false)] [InlineData(true, false, false)] [InlineData(true, true, false)] [InlineData(false, false, true)] [InlineData(false, true, true)] [InlineData(true, false, true)] [InlineData(true, true, true)]
    public async Task CompleteNativeWireAndSignature_MatchIndependentGoldens(bool arn, bool temporary, bool publicSdk)
    {
        var expected = JsonSerializer.Deserialize<JsonElement>(AwsFixture.Text("converse-signatures")).EnumerateArray().Single(item => item.GetProperty("arn").GetBoolean() == arn && item.GetProperty("temporary").GetBoolean() == temporary); var clock = new AwsClock(); var source = new AwsCredentialSource(clock); if (temporary) source.Resolve = (principal, _) => ValueTask.FromResult(new AwsLlmSigningCredentials(principal, AwsCredentialSource.KeyId, AwsCredentialSource.Secret, clock.Now.AddHours(1), "fixture-session")); var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(AwsFixture.Text("converse-response"))); using var handler = new StubHttpHandler(async (request, token) => { Assert.Equal(expected.GetProperty("uri").GetString(), request.RequestUri!.AbsoluteUri); Assert.Equal(expected.GetProperty("authorization").GetString(), Assert.Single(request.Headers.GetValues("Authorization"))); Assert.Equal(expected.GetProperty("payloadHash").GetString(), Assert.Single(request.Headers.GetValues("x-amz-content-sha256"))); Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType); Assert.False(request.Headers.Contains("x-amzn-bedrock-accept")); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(AwsFixture.Text("converse-wire")), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(body) }; }); var factory = new AwsHttpFactory(handler) { DefaultHeader = "Authorization" }; var headers = temporary ? new Dictionary<string, string> { ["X-Amzn-Bedrock-Trace"] = "ENABLED", ["x-amzn-bedrock-future-option"] = "  first   second  " } : []; var provider = new AwsLlmProvider(factory, source, new() { Region = "us-east-1", RequestHeaders = headers }, timeProvider: clock); var target = ConverseFixture.Target() with { UpstreamModel = arn ? "arn:aws:bedrock:us-east-1:123456789012:inference-profile/profile:0" : ConverseFixture.Model }; await using var client = ConverseFixture.Client(provider, targets: [target]); var input = ConverseFixture.Request(); var before = input.ToJsonString(); var result = publicSdk ? await ConverseFixture.Invoke(client) : await provider.ConverseAsync(input, ConverseFixture.Context(target), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(AwsFixture.Text("converse-response")), result.Data)); Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); Assert.Equal(before, input.ToJsonString()); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
    /// <summary>原生提示 ARN 请求可以省略 messages/inferenceConfig 等，不注入默认消息或预算。</summary>
    [Fact]
    public async Task PromptOnlyRequest_DoesNotInjectMessagesOrDefaults()
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler(async (request, token) => { Assert.Equal("https://first.invalid/proxy/model/arn%3Aaws%3Abedrock%3Aus-east-1%3A123456789012%3Aprompt%2FABCD123456%3A1/converse", request.RequestUri!.AbsoluteUri); Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"promptVariables":{"topic":{"text":"hello"}}}"""), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return ConverseFixture.Reply(); }); var provider = new AwsLlmProvider(new AwsHttpFactory(handler), source, new() { Region = "us-east-1" }, timeProvider: clock); await using var client = ConverseFixture.Client(provider, targets: [ConverseFixture.Target() with { UpstreamModel = "arn:aws:bedrock:us-east-1:123456789012:prompt/ABCD123456:1" }]); await client.LLM.BedrockConverse.ConverseAsync(JsonSerializer.Deserialize<BedrockConverseRequest>("""{"model":"public","promptVariables":{"topic":{"text":"hello"}}}""")!, new() { SessionId = "prompt-session" }, TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>Converse 没有 SDK stream 或 Anthropic 正文字段策略，显式未知值不因复用传输而丢失。</summary>
    [Fact]
    public async Task ExplicitUnknownFields_AreNotStrippedByClaudePolicy()
    {
        var clock = new AwsClock(); using var handler = new StubHttpHandler(async (request, token) => { Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"stream":null,"anthropic_version":"caller-owned","messages":null}"""), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return ConverseFixture.Reply(); }); await using var client = ConverseFixture.Client(new(new AwsHttpFactory(handler), new AwsCredentialSource(clock), new() { Region = "us-east-1", ClaudeAnthropicVersion = "unrelated-claude-setting" }, timeProvider: clock)); await client.LLM.BedrockConverse.ConverseAsync(JsonSerializer.Deserialize<BedrockConverseRequest>("""{"model":"public","stream":null,"anthropic_version":"caller-owned","messages":null}""")!, new() { SessionId = "native-unknown" }, TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>ConverseStream 预取消不取得来源或 HTTP 客户端。</summary>
    [Fact]
    public async Task ConverseStream_PreCancelledAcquiresNoResources()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); cancel.Cancel(); var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new AwsHttpFactory(handler); await using var client = ConverseFixture.Client(new(factory, source, new() { Region = "us-east-1" })); async Task Consume() { await foreach (var _ in client.LLM.BedrockConverse.StreamAsync(ConverseFixture.Request().Deserialize<BedrockConverseRequest>()!, new() { SessionId = "converse-stream" }, cancel.Token)) { } } await Assert.ThrowsAnyAsync<OperationCanceledException>(Consume); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
}
