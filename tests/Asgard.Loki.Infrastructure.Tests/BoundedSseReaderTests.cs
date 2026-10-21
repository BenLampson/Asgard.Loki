using System.Text;
using Xunit;

namespace Asgard.Loki.Infrastructure.Tests;

/// <summary>共享有界 SSE 只验证原始字节/帧/长度，不判断模型结果。</summary>
public sealed class BoundedSseReaderTests
{
    /// <summary>原始预算包含 BOM、注释、字段和 CRLF；多行 Unicode 数据、空数据和元数据保持原样。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExactWireBudget_RetainsWholeFrame(bool declared)
    {
        var bytes = Encoding.UTF8.GetBytes("\uFEFF: heartbeat\r\nid: native\r\nretry: 0\r\nevent: original\r\ndata: 你好🌏\r\ndata: second\r\n\r\ndata:\r\n\r\n");
        using var source = new SseProbeStream(bytes); var frames = await Read(source, bytes.Length, declared ? bytes.Length : null);
        Assert.Equal(2, frames.Count); Assert.Equal("你好🌏\nsecond", frames[0].Data); Assert.Equal("original", frames[0].EventName);
        Assert.Equal("native", frames[1].EventId); Assert.Equal(0, frames[1].RetryMilliseconds); Assert.Equal("", frames[1].Data); Assert.False(source.Disposed);
    }
    /// <summary>超限、过短或过长声明均失败；注释和原生 DONE 也不能绕过原始总预算。</summary>
    [Theory] [InlineData("read-limit")] [InlineData("short")] [InlineData("long")] [InlineData("declared-limit")]
    public async Task RawLimitAndLengthMismatch_FailExplicitly(string kind)
    {
        var bytes = Encoding.UTF8.GetBytes("data: [DONE]\n\n: tail-byte-budget\n\n"); using var source = new SseProbeStream(bytes);
        var maximum = kind is "read-limit" or "declared-limit" ? bytes.Length - 1 : bytes.Length + 10;
        long? length = kind == "read-limit" ? null : kind == "short" ? bytes.Length - 1 : kind == "long" ? bytes.Length + 1 : bytes.Length;
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(source, maximum, length)); Assert.False(source.Disposed);
        if (kind == "declared-limit") Assert.Equal(0, source.Reads);
    }
    /// <summary>空流/纯元数据不会合成事件；空原始声明长度仍与真实 EOF 匹配。</summary>
    [Theory] [InlineData("")] [InlineData(": heartbeat\nid: original\nretry: 4\n\n")]
    public async Task EmptyAndMetadataOnlyEof_ReturnNoSyntheticEvent(string wire)
    {
        var bytes = Encoding.UTF8.GetBytes(wire); using var source = new SseProbeStream(bytes);
        Assert.Empty(await Read(source, Math.Max(1, bytes.Length), bytes.Length)); Assert.False(source.Disposed);
    }
    /// <summary>无完整事件边界的尾部和非法 UTF-8 仍按原有严格帧语义失败，不能由字节预算修复。</summary>
    [Theory] [InlineData("truncated")] [InlineData("utf8")]
    public async Task StrictFramingAndEncoding_ArePreserved(string kind)
    {
        byte[] bytes = kind == "truncated" ? Encoding.UTF8.GetBytes("data: partial\n") : [.. Encoding.UTF8.GetBytes("data: "), 0xff, 10, 10];
        using var source = new SseProbeStream(bytes); var failure = await Record.ExceptionAsync(() => Read(source, bytes.Length));
        if (kind == "utf8") Assert.IsType<DecoderFallbackException>(failure); else Assert.IsType<InvalidDataException>(failure); Assert.False(source.Disposed);
    }
    /// <summary>原有默认宽松解码未改变，有界入口只有显式 strictUtf8 才拒绝坏编码。</summary>
    [Fact]
    public async Task DefaultDecoding_RemainsCompatible()
    {
        using var source = new SseProbeStream([.. Encoding.UTF8.GetBytes("data: "), 0xff, 10, 10]); List<SseFrame> frames = [];
        await foreach (var frame in SseReader.ReadBoundedAsync(source, 100, TestContext.Current.CancellationToken)) frames.Add(frame);
        Assert.Equal("\uFFFD", Assert.Single(frames).Data); Assert.False(source.Disposed);
    }
    /// <summary>非法预算/声明和空源在首次读取前拒绝。</summary>
    [Theory] [InlineData("zero")] [InlineData("negative")] [InlineData("negative-length")] [InlineData("null")]
    public async Task InvalidInput_FailsBeforeReads(string kind)
    {
        using var source = new SseProbeStream([1]);
        var failure = await Record.ExceptionAsync(() => Read(kind == "null" ? null! : source, kind == "zero" ? 0 : kind == "negative" ? -1 : 10, kind == "negative-length" ? -1 : null));
        if (kind == "null") Assert.IsType<ArgumentNullException>(failure); else Assert.IsAssignableFrom<ArgumentOutOfRangeException>(failure);
        Assert.Equal(0, source.Reads); Assert.False(source.Disposed);
    }
    /// <summary>预取消和 EOF 时取消不能变成普通枚举成功，源仍由调用者拥有。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CancellationBeforeReadOrAtEof_IsPropagated(bool preCancelled)
    {
        using var cancel = new CancellationTokenSource(); using var source = new SseProbeStream(Encoding.UTF8.GetBytes("data: original\n\n")) { OnEof = cancel.Cancel };
        if (preCancelled) cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(source, 100, token: cancel.Token));
        if (preCancelled) Assert.Equal(0, source.Reads); Assert.False(source.Disposed);
    }
    /// <summary>提前退出保留源所有权，交付帧后的取消也不能被缓冲掩盖。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task EarlyExitOrLateCancellation_LeavesSourceOpen(bool cancellation)
    {
        using var cancel = new CancellationTokenSource(); using var source = new SseProbeStream(Encoding.UTF8.GetBytes("data: first\n\ndata: second\n\n"));
        await using (var frames = SseReader.ReadBoundedAsync(source, 100, cancel.Token, strictUtf8: true).GetAsyncEnumerator(cancel.Token))
        {
            Assert.True(await frames.MoveNextAsync()); Assert.Equal("first", frames.Current.Data);
            if (cancellation) { cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await frames.MoveNextAsync()); }
        }
        Assert.False(source.Disposed);
    }
    /// <summary>源的 EOF 异常原样传播，不重读或关闭调用者流。</summary>
    [Fact]
    public async Task EofFailure_IsNotNormalCompletion()
    {
        var failure = new IOException("fixture EOF"); using var source = new SseProbeStream(Encoding.UTF8.GetBytes("data: original\n\n")) { OnEof = () => throw failure };
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Read(source, long.MaxValue))); Assert.False(source.Disposed);
    }
    private static async Task<List<SseFrame>> Read(Stream stream, long maximum, long? expected = null, CancellationToken? token = null)
    {
        List<SseFrame> result = [];
        await foreach (var frame in SseReader.ReadBoundedAsync(stream, maximum, token ?? TestContext.Current.CancellationToken, strictUtf8: true, expectedLength: expected)) result.Add(frame);
        return result;
    }
}
