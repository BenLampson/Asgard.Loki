using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>完整 Nova 双层信封和独立签名验证，不将原始事件规范化为 Converse 数据。</summary>
public sealed class NovaStreamingTests
{
    /// <summary>十六个独立签名用例覆盖 v1/v2、模型/ARN、临时凭据、直接/SDK。</summary>
    public static IEnumerable<object[]> SignedCases() => NovaNativeTests.SignedCases();
    /// <summary>最终 HTTP、全部原生帧和外层类型化头逐一匹配独立金样。</summary>
    [Theory] [MemberData(nameof(SignedCases))]
    public async Task CompleteStream_MatchesIndependentFramesAndSignature(string version, bool sdk, bool arn, bool temporary)
    {
        var vector = JsonSerializer.Deserialize<JsonElement>(AwsFixture.Text("nova-stream-signatures")).EnumerateArray().Single(item => item.GetProperty("version").GetString() == version && item.GetProperty("publicSdk").GetBoolean() == sdk && item.GetProperty("arn").GetBoolean() == arn && item.GetProperty("temporary").GetBoolean() == temporary);
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); if (temporary) source.Resolve = (principal, _) => ValueTask.FromResult(new AwsLlmSigningCredentials(principal, AwsCredentialSource.KeyId, AwsCredentialSource.Secret, clock.Now.AddHours(1), "fixture-session"));
        var wire = NovaStreamFixture.Bytes(); var body = new OllamaBodyStream(wire);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(vector.GetProperty("uri").GetString(), message.RequestUri!.AbsoluteUri); Assert.Equal(vector.GetProperty("body").GetString(), await message.Content!.ReadAsStringAsync(token)); Assert.Equal(vector.GetProperty("authorization").GetString(), Assert.Single(message.Headers.GetValues("Authorization")));
            Assert.Equal("application/vnd.amazon.eventstream", Assert.Single(message.Headers.Accept).MediaType); Assert.Equal("application/json", Assert.Single(message.Headers.GetValues("x-amzn-bedrock-accept"))); Assert.False(message.Headers.Contains("anthropic-version"));
            var reply = StreamFixture.Reply(body); reply.Content.Headers.ContentLength = wire.Length; return reply;
        });
        var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, source, new() { Region = "us-east-1", RequestHeaders = temporary ? new Dictionary<string, string> { ["x-amzn-bedrock-trace"] = "ENABLED" } : new Dictionary<string, string>() }, timeProvider: clock); var target = NovaFixture.Target(vector.GetProperty("model").GetString()!); var seen = new List<BedrockNovaEvent>();
        void Observe(BedrockNovaEvent item) { if (item.Type is "messageStop" or "metadata" || item.Data.TryGetProperty("vendor.tail", out _) || item.EventName == "vendor.outer") { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } }
        if (sdk) { await using var client = NovaFixture.Client(provider, targets: [target]); await foreach (var item in client.LLM.BedrockNova.StreamAsync(NovaFixture.Typed(version), new() { SessionId = "aws-session" }, TestContext.Current.CancellationToken)) { Observe(item); seen.Add(item); } }
        else await foreach (var item in provider.NovaStreamAsync(NovaFixture.Request(version), NovaFixture.Context(target), TestContext.Current.CancellationToken)) { Observe(item); seen.Add(item); }
        var events = NovaStreamFixture.Events(); var frames = JsonSerializer.Deserialize<JsonElement>(AwsFixture.Text("nova-stream-frames")); Assert.Equal(events.GetArrayLength(), seen.Count);
        for (var i = 0; i < seen.Count; i++)
        {
            Assert.True(JsonElement.DeepEquals(events[i].GetProperty("data"), seen[i].Data)); Assert.Equal(events[i].GetProperty("eventName").GetString(), seen[i].EventName); Assert.Equal(events[i].GetProperty("nativeType").GetString() ?? seen[i].EventName, ((LlmJsonEvent)seen[i]).Type); Assert.Null(seen[i].EventId); Assert.Null(seen[i].RetryMilliseconds);
            var frame = seen[i].GetAwsEventStreamFrame()!; var expected = frames[i]; Assert.True(JsonElement.DeepEquals(expected.GetProperty("headers"), frame.Headers)); Assert.Equal(Convert.FromBase64String(expected.GetProperty("payloadBase64").GetString()!), frame.Payload.ToArray()); Assert.Equal(wire.AsSpan(expected.GetProperty("offset").GetInt32(), expected.GetProperty("length").GetInt32()).ToArray(), frame.WireBytes.ToArray()); Assert.Equal(target.UpstreamModel, seen[i].Metadata.UpstreamModel);
        }
        Assert.Equal("9007199254740993", seen[0].Data.GetProperty("future").GetProperty("integer").GetRawText()); Assert.Equal("9007199254740993", seen[14].Data.GetProperty("metadata").GetProperty("usage").GetProperty("future").GetRawText()); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>同一原生流可以显式 Bearer 调用；既不发送 AWS 签名也不丢弃信封。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ApiKeyNativeStream_PreservesAllEnvelopes(bool sdk)
    {
        using var handler = new StubHttpHandler((message, _) => { Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("fixture-nova-key", message.Headers.Authorization.Parameter); Assert.False(message.Headers.Contains("x-amz-date")); Assert.False(message.Headers.Contains("x-amz-content-sha256")); return Task.FromResult(StreamFixture.Reply(NovaStreamFixture.Bytes())); });
        var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" }); List<BedrockNovaEvent> seen = [];
        if (sdk) { await using var client = NovaFixture.Client(provider, true); await foreach (var item in client.LLM.BedrockNova.StreamAsync(NovaFixture.Typed(), new() { SessionId = "aws-session" }, TestContext.Current.CancellationToken)) seen.Add(item); }
        else await NovaStreamFixture.Drain(provider, TestContext.Current.CancellationToken, seen, apiKey: true);
        var expected = NovaStreamFixture.Events(); Assert.Equal(expected.GetArrayLength(), seen.Count); for (var i = 0; i < seen.Count; i++) Assert.True(JsonElement.DeepEquals(expected[i].GetProperty("data"), seen[i].Data)); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
}
