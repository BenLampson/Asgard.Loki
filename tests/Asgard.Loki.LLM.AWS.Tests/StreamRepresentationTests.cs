using System.Net;
using System.Text;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>二进制内外层 JSON/Base64 与媒体边界不被当作可修复的原生成功。</summary>
public sealed class StreamRepresentationTests
{
    /// <summary>完整有效帧中的无效载荷必须拒绝，并提供原始帧而不静默丢弃未知内容。</summary>
    [Theory] [InlineData("base64")] [InlineData("no-bytes")] [InlineData("outer-array")] [InlineData("outer-duplicate")] [InlineData("inner-duplicate")] [InlineData("inner-escape")] [InlineData("inner-utf8")] [InlineData("unknown-binary")] [InlineData("message-type")]
    public async Task InvalidPayload_PreservesFrameOnFailure(string scenario)
    {
        var wire = scenario switch
        {
            "base64" => StreamFixture.Frame("event", "chunk", "{\"bytes\":\"?\"}"), "no-bytes" => StreamFixture.Frame("event", "chunk", "{\"future\":true}"), "outer-array" => StreamFixture.Frame("event", "chunk", "[]"), "outer-duplicate" => StreamFixture.Frame("event", "chunk", "{\"bytes\":\"e30=\",\"bytes\":\"e30=\"}"), "inner-duplicate" => StreamFixture.Chunk("{\"type\":\"ping\",\"future\":1,\"future\":2}"), "inner-escape" => StreamFixture.Chunk("{\"type\":\"ping\",\"future\":\"\\uD800\"}"), "inner-utf8" => StreamFixture.Frame("event", "chunk", "{\"bytes\":\"/w==\"}"), "unknown-binary" => StreamFixture.Frame("event", "future_binary", "original unknown payload", media: "application/octet-stream"), _ => StreamFixture.Frame("unrecognized", "future", "{}")
        };
        var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(wire))); var observed = 0; var error = await Record.ExceptionAsync(() => StreamFixture.Consume(new(new AwsHttpFactory(handler), new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock), TestContext.Current.CancellationToken, _ => observed++)); Assert.NotNull(error); Assert.Equal(0, observed); Assert.NotNull(error.GetAwsEventStreamFrame()); Assert.Equal(wire, error.GetAwsEventStreamFrame()!.WireBytes.ToArray()); Assert.True(error is InvalidDataException or FormatException or System.Text.Json.JsonException or DecoderFallbackException or NotSupportedException);
    }
    /// <summary>普通 JSON/SSE 不能假冒 AWS 二进制流，模型内容类型也必须明确为 JSON。</summary>
    [Theory] [InlineData("json")] [InlineData("sse")] [InlineData("model-media")] [InlineData("model-media-duplicate")]
    public async Task InvalidMedia_IsRejected(string scenario)
    {
        var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) =>
        {
            if (scenario is "json" or "sse") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, scenario == "json" ? "application/json" : "text/event-stream") }); var reply = StreamFixture.Reply(StreamFixture.Bytes()); reply.Headers.Remove("x-amzn-bedrock-content-type"); if (scenario == "model-media") reply.Headers.Add("x-amzn-bedrock-content-type", "text/plain"); else reply.Headers.Add("x-amzn-bedrock-content-type", ["application/json", "application/json"]); return Task.FromResult(reply);
        }); var factory = new AwsHttpFactory(handler); await Assert.ThrowsAsync<InvalidDataException>(() => StreamFixture.Consume(new(factory, new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock), TestContext.Current.CancellationToken)); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
