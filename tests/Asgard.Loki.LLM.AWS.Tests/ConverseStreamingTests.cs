using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>Converse 独立直接 JSON 事件/原始帧金样，不复用 Claude 内层协议。</summary>
public sealed class ConverseStreamingTests
{
    /// <summary>完整请求、签名、事件、全部类型化头和原始载荷逐项匹配独立金样。</summary>
    [Theory] [InlineData(false, false, false)] [InlineData(false, true, false)] [InlineData(true, false, false)] [InlineData(true, true, false)] [InlineData(false, false, true)] [InlineData(false, true, true)] [InlineData(true, false, true)] [InlineData(true, true, true)]
    public async Task NativeConverseFrames_MatchFullIndependentGoldens(bool arn, bool temporary, bool publicSdk)
    {
        var expected = JsonSerializer.Deserialize<JsonElement>(AwsFixture.Text("converse-stream-signatures")).EnumerateArray().Single(item => item.GetProperty("arn").GetBoolean() == arn && item.GetProperty("temporary").GetBoolean() == temporary); var clock = new AwsClock(); var source = new AwsCredentialSource(clock); if (temporary) source.Resolve = (principal, _) => ValueTask.FromResult(new AwsLlmSigningCredentials(principal, AwsCredentialSource.KeyId, AwsCredentialSource.Secret, clock.Now.AddHours(1), "fixture-session")); var wire = ConverseStreamFixture.Bytes(); var body = new OllamaBodyStream(wire); using var handler = new StubHttpHandler(async (request, token) => { Assert.Equal(expected.GetProperty("uri").GetString(), request.RequestUri!.AbsoluteUri); Assert.Equal(expected.GetProperty("authorization").GetString(), Assert.Single(request.Headers.GetValues("Authorization"))); Assert.Equal(expected.GetProperty("payloadHash").GetString(), Assert.Single(request.Headers.GetValues("x-amz-content-sha256"))); Assert.Equal("application/vnd.amazon.eventstream", Assert.Single(request.Headers.Accept).MediaType); Assert.False(request.Headers.Contains("x-amzn-bedrock-accept")); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(AwsFixture.Text("converse-wire")), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return StreamFixture.Reply(body); }); var factory = new AwsHttpFactory(handler) { DefaultHeader = "Authorization" }; var headers = temporary ? new Dictionary<string, string> { ["X-Amzn-Bedrock-Trace"] = "ENABLED", ["x-amzn-bedrock-future-option"] = "  first   second  " } : []; var provider = new AwsLlmProvider(factory, source, new() { Region = "us-east-1", RequestHeaders = headers }, timeProvider: clock); var target = ConverseFixture.Target() with { UpstreamModel = arn ? "arn:aws:bedrock:us-east-1:123456789012:inference-profile/profile:0" : ConverseFixture.Model }; await using var client = ConverseFixture.Client(provider, targets: [target]); var events = JsonSerializer.Deserialize<JsonElement>(AwsFixture.Text("converse-stream-events")); var frames = JsonSerializer.Deserialize<JsonElement>(AwsFixture.Text("converse-stream-frames")); var input = ConverseFixture.Request(); var before = input.ToJsonString(); var index = 0;
        var call = publicSdk ? client.LLM.BedrockConverse.StreamAsync(input.Deserialize<BedrockConverseRequest>()!, new() { SessionId = "converse-session" }, TestContext.Current.CancellationToken) : provider.ConverseStreamAsync(input, ConverseFixture.Context(target), TestContext.Current.CancellationToken);
        await foreach (var item in call)
        {
            Assert.Equal(events[index].GetProperty("eventName").GetString(), item.EventName); Assert.Equal(item.EventName, item.Type); Assert.True(JsonElement.DeepEquals(events[index].GetProperty("data"), item.Data)); Assert.Null(item.EventId); Assert.Null(item.RetryMilliseconds); Assert.Equal(index >= 24, body.Eof); Assert.Equal(index >= 24, body.Disposed); Assert.Equal(index >= 24, Assert.Single(factory.Clients).Disposed); var frame = item.GetAwsEventStreamFrame(); Assert.NotNull(frame); Assert.True(JsonElement.DeepEquals(frames[index].GetProperty("headers"), frame.Headers)); Assert.Equal(Convert.FromBase64String(frames[index].GetProperty("payloadBase64").GetString()!), frame.Payload.ToArray()); Assert.Equal(wire.AsSpan(frames[index].GetProperty("offset").GetInt32(), frames[index].GetProperty("length").GetInt32()).ToArray(), frame.WireBytes.ToArray()); Assert.Equal(target.UpstreamModel, item.Metadata.UpstreamModel); index++;
        }
        Assert.Equal(27, index); Assert.Equal(before, input.ToJsonString()); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>首次来源等待之前复制请求，等待期间修改不改变已选上网正文。</summary>
    [Fact]
    public async Task StreamSnapshot_PrecedesCredentialAwait()
    {
        var clock = new AwsClock(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var source = new AwsCredentialSource(clock) { Resolve = async (principal, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return new(principal, AwsCredentialSource.KeyId, AwsCredentialSource.Secret, clock.Now.AddHours(1)); } }; using var handler = new StubHttpHandler(async (request, token) => { Assert.True(JsonNode.DeepEquals(JsonNode.Parse(AwsFixture.Text("converse-wire")), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return StreamFixture.Reply(ConverseStreamFixture.Bytes()); }); var input = ConverseFixture.Request(); var provider = new AwsLlmProvider(new AwsHttpFactory(handler), source, new() { Region = "us-east-1" }, timeProvider: clock); await using var iterator = provider.ConverseStreamAsync(input, ConverseFixture.Context(), TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken); var first = iterator.MoveNextAsync().AsTask(); await entered.Task.WaitAsync(TestContext.Current.CancellationToken); input["messages"] = new JsonArray(); release.SetResult(); Assert.True(await first); while (await iterator.MoveNextAsync()) { } Assert.Equal(1, handler.Calls);
    }
    /// <summary>新原生事件的 type/error/bytes 字段不继承 Claude chunk 解码和错误语义。</summary>
    [Fact]
    public async Task UnknownTransportJson_IsNativeNotClaudeEnvelope()
    {
        const string json = "{\"bytes\":\"not-base64\",\"type\":\"error\",\"error\":{\"future\":null}}"; var wire = new[] { ConverseStreamFixture.Start(), ConverseStreamFixture.Stop(), ConverseStreamFixture.Metadata(), ConverseStreamFixture.Event("chunk", json) }.SelectMany(item => item).ToArray(); var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(wire))); var observed = new List<BedrockConverseEvent>(); await ConverseStreamFixture.Consume(new(new AwsHttpFactory(handler), new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock), TestContext.Current.CancellationToken, observed.Add); Assert.Equal("chunk", observed[^1].Type); Assert.Equal("not-base64", observed[^1].Data.GetProperty("bytes").GetString()); Assert.Equal(System.Text.Encoding.UTF8.GetBytes(json), observed[^1].GetAwsEventStreamFrame()!.Payload.ToArray());
    }
}
