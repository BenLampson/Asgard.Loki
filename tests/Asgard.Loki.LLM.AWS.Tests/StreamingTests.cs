using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>独立完整二进制帧、内外层 JSON 和最终签名验证原生调用保真。</summary>
public sealed class StreamingTests
{
    /// <summary>公共 SDK/直接适配器均保留 Claude JSON 和全部外层帧元数据，终态在释放后交付。</summary>
    [Theory] [InlineData(false, false, false)] [InlineData(false, true, false)] [InlineData(true, false, false)] [InlineData(true, true, false)] [InlineData(false, false, true)] [InlineData(false, true, true)] [InlineData(true, false, true)] [InlineData(true, true, true)]
    public async Task NativeBinaryWireAndFrames_MatchIndependentGoldens(bool arn, bool temporary, bool publicSdk)
    {
        var signature = JsonSerializer.Deserialize<JsonElement>(AwsFixture.Text("stream-signatures")).EnumerateArray().Single(item => item.GetProperty("arn").GetBoolean() == arn && item.GetProperty("temporary").GetBoolean() == temporary && item.GetProperty("publicSdk").GetBoolean() == publicSdk); var clock = new AwsClock(); var source = new AwsCredentialSource(clock); if (temporary) source.Resolve = (principal, _) => ValueTask.FromResult(new AwsLlmSigningCredentials(principal, AwsCredentialSource.KeyId, AwsCredentialSource.Secret, clock.Now.AddHours(1), "fixture-session")); var wire = StreamFixture.Bytes(); var body = new OllamaBodyStream(wire);
        using var handler = new StubHttpHandler(async (request, token) => { Assert.Equal(signature.GetProperty("uri").GetString(), request.RequestUri!.AbsoluteUri); Assert.Equal(signature.GetProperty("authorization").GetString(), Assert.Single(request.Headers.GetValues("Authorization"))); Assert.Equal(signature.GetProperty("payloadHash").GetString(), Assert.Single(request.Headers.GetValues("x-amz-content-sha256"))); Assert.Equal("application/vnd.amazon.eventstream", Assert.Single(request.Headers.Accept).MediaType); Assert.Equal("application/json", Assert.Single(request.Headers.GetValues("x-amzn-bedrock-accept"))); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(AwsFixture.Text("wire")), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return StreamFixture.Reply(body); }); var factory = new AwsHttpFactory(handler) { DefaultHeader = "Authorization" }; var headers = temporary ? new Dictionary<string, string> { ["X-Amzn-Bedrock-Trace"] = "ENABLED", ["x-amzn-bedrock-future-option"] = "  first   second  " } : []; var provider = new AwsLlmProvider(factory, source, new() { Region = "us-east-1", RequestHeaders = headers }, timeProvider: clock); var target = AwsFixture.Target() with { UpstreamModel = arn ? "arn:aws:bedrock:us-east-1:123456789012:inference-profile/profile:0" : AwsFixture.Model }; await using var client = AwsFixture.Client(provider, targets: [target]); var input = AwsFixture.Request(); var before = input.ToJsonString(); var expected = JsonSerializer.Deserialize<JsonElement>(AwsFixture.Text("stream-events")); var frames = JsonSerializer.Deserialize<JsonElement>(AwsFixture.Text("stream-frames")); var index = 0;
        var call = publicSdk ? client.LLM.ClaudeMessages.StreamAsync(input.Deserialize<ClaudeMessageRequest>()!, new() { SessionId = "aws-session" }, TestContext.Current.CancellationToken) : provider.StreamAsync(input, AwsFixture.Context(target), TestContext.Current.CancellationToken);
        await foreach (var item in call)
        {
            Assert.True(JsonElement.DeepEquals(expected[index], item.Data)); Assert.Equal(index == 14 ? "future_transport" : "chunk", item.EventName); Assert.Null(item.EventId); Assert.Null(item.RetryMilliseconds); Assert.Equal(index >= 11, body.Eof); Assert.Equal(index >= 11, body.Disposed); Assert.Equal(index >= 11, Assert.Single(factory.Clients).Disposed); Assert.Equal(target.UpstreamModel, item.Metadata.UpstreamModel); var frame = item.GetAwsEventStreamFrame(); Assert.NotNull(frame); Assert.True(JsonElement.DeepEquals(frames[index].GetProperty("headers"), frame.Headers)); Assert.Equal(Convert.FromBase64String(frames[index].GetProperty("payloadBase64").GetString()!), frame.Payload.ToArray()); Assert.Equal(wire.AsSpan(frames[index].GetProperty("offset").GetInt32(), frames[index].GetProperty("length").GetInt32()).ToArray(), frame.WireBytes.ToArray()); index++;
        }
        Assert.Equal(16, index); Assert.Equal(before, input.ToJsonString()); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.True(body.Disposed); Assert.Equal("Asgard.Loki.LLM.AWS", Assert.Single(factory.Names)); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
    /// <summary>流式调用在第一次凭据等待之前拍摄原生请求快照。</summary>
    [Fact]
    public async Task StreamSnapshot_PrecedesSourceAwait()
    {
        var clock = new AwsClock(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var source = new AwsCredentialSource(clock) { Resolve = async (principal, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return new(principal, AwsCredentialSource.KeyId, AwsCredentialSource.Secret, clock.Now.AddHours(1)); } }; using var handler = new StubHttpHandler(async (request, token) => { Assert.True(JsonNode.DeepEquals(JsonNode.Parse(AwsFixture.Text("wire")), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return StreamFixture.Reply(StreamFixture.Bytes()); }); var input = AwsFixture.Request(); var provider = new AwsLlmProvider(new AwsHttpFactory(handler), source, new() { Region = "us-east-1" }, timeProvider: clock); await using var iterator = provider.StreamAsync(input, AwsFixture.Context(), TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken); var first = iterator.MoveNextAsync().AsTask(); await entered.Task.WaitAsync(TestContext.Current.CancellationToken); input["messages"] = new JsonArray(); release.SetResult(); Assert.True(await first); while (await iterator.MoveNextAsync()) { } Assert.Equal(1, handler.Calls);
    }
    /// <summary>帧视图的二进制访问均独立复制，修改调用方数组不能破坏内部快照。</summary>
    [Fact]
    public async Task FrameViews_AreImmutableAndProviderLocal()
    {
        var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(StreamFixture.Bytes()))); var provider = new AwsLlmProvider(new AwsHttpFactory(handler), new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock); await foreach (var item in provider.StreamAsync(AwsFixture.Request(), AwsFixture.Context(), TestContext.Current.CancellationToken))
        {
            var frame = item.GetAwsEventStreamFrame()!; var original = frame.WireBytes.ToArray(); Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(frame.WireBytes, out var exposed)); exposed.Array![exposed.Offset] ^= 255; Assert.Equal(original, frame.WireBytes.ToArray()); Assert.Null(new ClaudeMessageEvent(item.Data, item.Metadata).GetAwsEventStreamFrame()); Assert.Null(new IOException("unrelated").GetAwsEventStreamFrame()); break;
        }
    }
}
