using System.Buffers.Binary;
using System.Text;
using Asgard.Loki.LLM.AWS.Internal;
using Asgard.Loki.LLM.Ollama.Tests;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>二进制解码器单独验证，不能依赖 Claude 状态错误来掩盖未检查的 CRC/头长度。</summary>
public sealed class FrameReaderTests
{
    /// <summary>有效预期 CRC 之外的畸形帧不能产生任何已验证帧。</summary>
    [Theory] [InlineData("prelude-crc")] [InlineData("message-crc")] [InlineData("short-prelude")] [InlineData("short-frame")] [InlineData("length-underflow")] [InlineData("headers-overflow")] [InlineData("huge-frame")] [InlineData("duplicate-header")] [InlineData("unknown-type")] [InlineData("empty-name")] [InlineData("name-utf8")] [InlineData("value-utf8")] [InlineData("truncated-value")] [InlineData("truncated-uuid")]
    public async Task MalformedFrame_IsRejectedBeforePublication(string scenario)
    {
        var wire = StreamFixture.Frame("event", "future", "{}");
        switch (scenario)
        {
            case "prelude-crc": wire[8] ^= 1; break;
            case "message-crc": wire[^1] ^= 1; break;
            case "short-prelude": wire = wire[..7]; break;
            case "short-frame": wire = wire[..^1]; break;
            case "length-underflow": BinaryPrimitives.WriteUInt32BigEndian(wire, 15); StreamFixture.FixPrelude(wire); break;
            case "headers-overflow": BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(4), (uint)wire.Length); StreamFixture.FixPrelude(wire); break;
            case "huge-frame": BinaryPrimitives.WriteUInt32BigEndian(wire, uint.MaxValue); StreamFixture.FixPrelude(wire); break;
            case "duplicate-header": wire = StreamFixture.Frame("event", "future", "{}", ":event-type", "duplicate"); break;
            case "unknown-type": wire = StreamFixture.Wire([1, (byte)'x', 10], []); break;
            case "empty-name": wire = StreamFixture.Wire([0, 7, 0, 1, 65], []); break;
            case "name-utf8": wire = StreamFixture.Wire([1, 255, 7, 0, 1, 65], []); break;
            case "value-utf8": wire = StreamFixture.Wire([1, (byte)'x', 7, 0, 1, 255], []); break;
            case "truncated-value": wire = StreamFixture.Wire([1, (byte)'x', 7, 0, 5, 65], []); break;
            case "truncated-uuid": wire = StreamFixture.Wire([1, (byte)'x', 9, 0], []); break;
        }
        using var source = new OllamaBodyStream(wire); var observed = 0; async Task Consume() { await foreach (var _ in AwsEventStreamReader.ReadAsync(source, 8192, 8192, null, TestContext.Current.CancellationToken)) observed++; } var error = await Record.ExceptionAsync(Consume); Assert.NotNull(error); Assert.True(error is InvalidDataException or DecoderFallbackException); Assert.Equal(0, observed);
    }
    /// <summary>本地帧/总字节和原始 Content-Length 各自限制，不与服务端最大值混淆。</summary>
    [Theory] [InlineData("exact")] [InlineData("frame")] [InlineData("total")] [InlineData("short")] [InlineData("long")]
    public async Task ExplicitLocalBoundsAndContentLength_AreExact(string scenario)
    {
        var wire = StreamFixture.Frame("event", "future", "{}"); using var source = new OllamaBodyStream(wire); var observed = 0; async Task Consume() { await foreach (var item in AwsEventStreamReader.ReadAsync(source, wire.Length - (scenario == "frame" ? 1 : 0), wire.Length - (scenario == "total" ? 1 : 0), wire.Length + (scenario == "short" ? -1 : scenario == "long" ? 1 : 0), TestContext.Current.CancellationToken)) { Assert.Equal(wire, item.WireBytes.ToArray()); observed++; } }
        if (scenario == "exact") { await Consume(); Assert.Equal(1, observed); } else { await Assert.ThrowsAsync<InvalidDataException>(Consume); Assert.Equal(scenario == "long" ? 1 : 0, observed); }
    }
}
