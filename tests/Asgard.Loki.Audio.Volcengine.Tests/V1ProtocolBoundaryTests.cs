using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace Asgard.Loki.Audio.Volcengine.Tests;

/// <summary>独立样本单点损坏及资源策略验证，不测试编码器自我往返。</summary>
public sealed class V1ProtocolBoundaryTests
{
    /// <summary>未知协议、长度、符号、gzip 与关联错误必须显式失败。</summary>
    [Theory]
    [InlineData("short")]
    [InlineData("version")]
    [InlineData("header-size")]
    [InlineData("reserved")]
    [InlineData("type")]
    [InlineData("flags")]
    [InlineData("serialization")]
    [InlineData("compression")]
    [InlineData("length-large")]
    [InlineData("length-small")]
    [InlineData("trailing")]
    [InlineData("positive-zero")]
    [InlineData("positive-negative")]
    [InlineData("final-positive")]
    [InlineData("final-zero")]
    [InlineData("gzip-crc")]
    [InlineData("gzip-size")]
    [InlineData("gzip-truncated")]
    [InlineData("metadata-reqid")]
    [InlineData("metadata-duplicate")]
    [InlineData("metadata-unicode")]
    [InlineData("metadata-invalid-json")]
    public async Task InvalidPackets(string kind)
    {
        var raw = V1Fixture.Frame(kind.StartsWith("gzip", StringComparison.Ordinal) ? "audio-gzip-1" : kind.StartsWith("final", StringComparison.Ordinal) ? "audio-final-3" : "audio-1");
        switch (kind)
        {
            case "short": raw = raw[..3]; break;
            case "version": raw[0] = 0x21; break;
            case "header-size": raw[0] = 0x12; break;
            case "reserved": raw[3] = 1; break;
            case "type": raw[1] = 0x90; break;
            case "flags": raw[1] = 0xb4; break;
            case "serialization": raw[2] = 0x10; break;
            case "compression": raw[2] = 2; break;
            case "length-large": raw[11]++; break;
            case "length-small": raw[11]--; break;
            case "trailing": raw = [.. raw, 0]; break;
            case "positive-zero": BinaryPrimitives.WriteInt32BigEndian(raw.AsSpan(4), 0); break;
            case "positive-negative": BinaryPrimitives.WriteInt32BigEndian(raw.AsSpan(4), -1); break;
            case "final-positive": BinaryPrimitives.WriteInt32BigEndian(raw.AsSpan(4), 1); break;
            case "final-zero": BinaryPrimitives.WriteInt32BigEndian(raw.AsSpan(4), 0); break;
            case "gzip-crc": raw[^8] ^= 1; break;
            case "gzip-size": raw[^4] ^= 1; break;
            case "gzip-truncated": raw = raw[..^4]; BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(8), (uint)raw.Length - 12); break;
            case "metadata-reqid": raw = Metadata("{\"reqid\":\"another-request\"}"); break;
            case "metadata-duplicate": raw = Metadata("{\"x\":1,\"x\":2}"); break;
            case "metadata-unicode": raw = Metadata("{\"x\":\"\\uD800\"}"); break;
            case "metadata-invalid-json": raw = Metadata("{"); break;
        }
        var socket = new V1Socket(); socket.Enqueue(raw); socket.Enqueue(V1Fixture.Frame("audio-final-3"));
        await Assert.ThrowsAnyAsync<Exception>(() => V1Fixture.Collect(new VolcengineV1SpeechAdapter(V1Fixture.Options(), webSocketFactory: socket)
            .StreamNativeAsync(V1Fixture.Request(), TestContext.Current.CancellationToken)));
        Assert.Equal(1, socket.Calls); Assert.Equal(1, socket.Disposals);
    }
    /// <summary>限制消息、解码、累计字节和空帧数，任何上限失败均不发布终态。</summary>
    [Theory]
    [InlineData("message")]
    [InlineData("decoded")]
    [InlineData("raw-total")]
    [InlineData("decoded-total")]
    [InlineData("frames")]
    public async Task ResponseBudgets(string kind)
    {
        var socket = kind == "decoded-total" ? new V1Socket("frontend-gzip", "frontend-gzip", "audio-final-3") : new V1Socket("audio-1", "audio-2", "audio-final-3");
        var options = V1Fixture.Options(message: kind == "message" ? 12 : 4096, decoded: kind == "decoded" ? 1 : 4096,
            total: kind == "raw-total" ? 20 : kind == "decoded-total" ? 500 : 65536, frames: kind == "frames" ? 1 : 50);
        var seen = new List<SpeechChunk>();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var chunk in new VolcengineV1SpeechAdapter(options, webSocketFactory: socket)
                .StreamNativeAsync(V1Fixture.Request(), TestContext.Current.CancellationToken)) seen.Add(chunk);
        });
        Assert.All(seen, x => Assert.False(x.IsCompleted)); Assert.Equal(1, socket.Disposals);
    }
    /// <summary>前端原始非 JSON 元数据仍完整保留，音频序号不强制从一连续增长。</summary>
    [Fact]
    public async Task RawMetadataAndNoncontiguousSequence()
    {
        var metadata = Metadata("untyped bytes"); metadata[2] = 0;
        var audio = V1Fixture.Frame("audio-1"); BinaryPrimitives.WriteInt32BigEndian(audio.AsSpan(4), 7);
        var socket = new V1Socket(); socket.Enqueue(audio); socket.Enqueue(metadata); socket.Enqueue(V1Fixture.Frame("audio-final-3"));
        var chunks = await V1Fixture.Collect(new VolcengineV1SpeechAdapter(V1Fixture.Options(), webSocketFactory: socket)
            .StreamNativeAsync(V1Fixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(metadata, Convert.FromBase64String(chunks[1].Data.GetProperty("rawFrameBase64").GetString()!));
        Assert.Equal(7, chunks[0].Data.GetProperty("sequence").GetInt32());
    }

    /// <summary>消息字节数以内的极端碎片仍受独立分片次数限制。</summary>
    [Fact]
    public async Task FragmentBudget()
    {
        var socket = new V1Socket("audio-final-3") { FragmentSize = 1 };
        await Assert.ThrowsAsync<InvalidDataException>(() => V1Fixture.Collect(new VolcengineV1SpeechAdapter(V1Fixture.Options(frames: 5), webSocketFactory: socket)
            .StreamNativeAsync(V1Fixture.Request(), TestContext.Current.CancellationToken)));
        Assert.Equal(1, socket.Disposals);
    }
    internal static byte[] Metadata(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text); var raw = new byte[8 + payload.Length]; raw[0] = 0x11; raw[1] = 0xc0; raw[2] = 0x10;
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(4), (uint)payload.Length); payload.CopyTo(raw, 8); return raw;
    }
}
