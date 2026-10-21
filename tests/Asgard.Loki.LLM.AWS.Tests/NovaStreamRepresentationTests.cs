using System.Net;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>Nova 二进制与双层 JSON 的错误边界，原始有效帧在异常上仍可取得。</summary>
public sealed class NovaStreamRepresentationTests
{
    /// <summary>外层/内层重复属性、畸形 Base64/JSON、非对象和二进制类型不能被修复成成功。</summary>
    [Theory]
    [InlineData("outer-duplicate")] [InlineData("inner-duplicate")] [InlineData("base64")]
    [InlineData("array")] [InlineData("escape")] [InlineData("missing-bytes")] [InlineData("binary")]
    public async Task MalformedChunk_FailsWithOriginalValidFrame(string fault)
    {
        var frame = fault switch
        {
            "outer-duplicate" => StreamFixture.Frame("event", "chunk", "{\"bytes\":\"e30=\",\"bytes\":\"e30=\"}"),
            "inner-duplicate" => NovaStreamFixture.Chunk("{\"future\":{\"n\":1,\"n\":2}}"),
            "base64" => StreamFixture.Frame("event", "chunk", "{\"bytes\":\"not-base64!\"}"),
            "array" => NovaStreamFixture.Chunk("[]"),
            "escape" => NovaStreamFixture.Chunk("{\"future\":\"\\uD800\"}"),
            "missing-bytes" => StreamFixture.Frame("event", "chunk", "{\"future\":false}"),
            _ => StreamFixture.Frame("event", "vendor.binary", "opaque", media: "application/octet-stream")
        };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(frame))); List<BedrockNovaEvent> seen = [];
        var failure = await Record.ExceptionAsync(() => NovaStreamFixture.Drain(new(new AwsHttpFactory(handler), new AwsLlmOptions { Region = "us-east-1" }), TestContext.Current.CancellationToken, seen, apiKey: true));
        Assert.NotNull(failure); Assert.True(failure is InvalidDataException or JsonException or FormatException or NotSupportedException, failure.ToString()); Assert.Equal(frame, failure.GetAwsEventStreamFrame()!.WireBytes.ToArray()); Assert.Empty(seen); Assert.Equal(1, handler.Calls);
    }
    /// <summary>成功候选之后的 CRC 或截断尾部不能被之前的原生 stop 遮盖。</summary>
    [Theory] [InlineData("crc")] [InlineData("prelude")] [InlineData("partial")]
    public async Task InvalidBinaryTail_PreventsNativeSuccess(string fault)
    {
        var tail = StreamFixture.Frame("event", "vendor.tail", "{}"); if (fault == "crc") tail[^1] ^= 1; else if (fault == "prelude") tail[8] ^= 1; else tail = tail[..^1];
        var wire = new byte[] { }.Concat(NovaStreamFixture.Complete()).Concat(tail).ToArray(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(wire))); List<BedrockNovaEvent> seen = [];
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => NovaStreamFixture.Drain(new(new AwsHttpFactory(handler), new AwsLlmOptions { Region = "us-east-1" }), TestContext.Current.CancellationToken, seen, apiKey: true));
        Assert.Null(failure.GetAwsEventStreamFrame()); Assert.DoesNotContain(seen, item => item.Type is "messageStop" or "metadata"); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生传输错误、服务事件及内层错误信封保留原始状态、正文与有效帧。</summary>
    [Theory] [InlineData("exception")] [InlineData("error")] [InlineData("event")] [InlineData("inner")]
    public async Task NativeServiceErrors_DiscardHeldSuccess(string kind)
    {
        const string body = "{\"message\":\"native failure\",\"future\":false}";
        var error = kind == "inner" ? NovaStreamFixture.Chunk("{\"modelStreamErrorException\":" + body + "}") : StreamFixture.Frame(kind, "modelStreamErrorException", body);
        var wire = new byte[] { }.Concat(NovaStreamFixture.Complete()).Concat(error).ToArray(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(wire))); List<BedrockNovaEvent> seen = [];
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => NovaStreamFixture.Drain(new(new AwsHttpFactory(handler), new AwsLlmOptions { Region = "us-east-1" }), TestContext.Current.CancellationToken, seen, apiKey: true));
        Assert.Equal(HttpStatusCode.OK, failure.StatusCode); Assert.Equal(kind == "inner" ? "{\"modelStreamErrorException\":" + body + "}" : body, failure.ResponseBody); Assert.Equal(error, failure.GetAwsEventStreamFrame()!.WireBytes.ToArray()); Assert.DoesNotContain(seen, item => item.Type is "messageStop" or "metadata"); Assert.Equal(1, handler.Calls);
    }
    /// <summary>视图每次返回独立原始字节，构造新事件不会误继承源帧关联。</summary>
    [Fact]
    public async Task RawFrameView_IsImmutableAndScopedToOriginalEvent()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(NovaStreamFixture.Complete()))); var provider = new AwsLlmProvider(new AwsHttpFactory(handler), new AwsLlmOptions { Region = "us-east-1" });
        await foreach (var item in provider.NovaStreamAsync(NovaFixture.Request(), NovaFixture.Context(apiKey: true), TestContext.Current.CancellationToken))
        {
            var raw = item.GetAwsEventStreamFrame()!; var expected = raw.WireBytes.ToArray(); Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(raw.WireBytes, out var exposed)); exposed.Array![exposed.Offset] ^= 255; Assert.Equal(expected, raw.WireBytes.ToArray());
            Assert.Null(new BedrockNovaEvent(item.Data, item.Metadata, item.EventName, item.Type).GetAwsEventStreamFrame()); break;
        }
    }
}
