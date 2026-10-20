using System.Text;
using Xunit;

namespace Asgard.Loki.Infrastructure.Tests;

/// <summary>公共 SSE 传输契约的离线测试，不混入任何模型生命周期判定。</summary>
public sealed class SseReaderTests
{
    /// <summary>完整事件保留多行正文、未知原生内容、事件身份和仅作数据的重连提示。</summary>
    [Theory] [InlineData("\n")] [InlineData("\r\n")] [InlineData("\r")]
    public async Task Events_PreserveDataAndInheritedTransportFields(string newline)
    {
        var source = string.Join(newline, [": heartbeat", "id: original", "retry: 0", "event: vendor.future", "data: {\"native\":false,", "data: \"optional\":null}", "", "data:", "", ""]);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));
        var frames = await Read(stream);
        Assert.Equal(2, frames.Count); Assert.Equal("{\"native\":false,\n\"optional\":null}", frames[0].Data);
        Assert.Equal("vendor.future", frames[0].EventName); Assert.Equal("original", frames[0].EventId); Assert.Equal(0, frames[0].RetryMilliseconds);
        Assert.Equal("", frames[1].Data); Assert.Null(frames[1].EventName); Assert.Equal("original", frames[1].EventId); Assert.Equal(0, frames[1].RetryMilliseconds);
        Assert.True(stream.CanRead);
    }
    /// <summary>无效 ID/retry 字段不得覆写先前有效的传输身份。</summary>
    [Theory] [InlineData("-1")] [InlineData("+1")] [InlineData("999999999999999999999")] [InlineData("١")]
    public async Task InvalidRetryAndNullId_KeepPreviousValues(string retry)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"id: original\nretry: 7\ndata: first\n\nid: ignored\0id\nretry: {retry}\ndata: second\n\n"));
        var frames = await Read(stream); Assert.Equal(2, frames.Count);
        Assert.Equal("original", frames[1].EventId); Assert.Equal(7, frames[1].RetryMilliseconds);
    }
    /// <summary>只去掉字段冒号后一个空格，空 ID 仍有独立语义。</summary>
    [Fact]
    public async Task LeadingSpacesAndEmptyId_ArePreserved()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("id: old\ndata: first\n\nid:\nunknown: metadata\ndata:  two spaces\n\n"));
        var frames = await Read(stream); Assert.Equal(" two spaces", frames[1].Data); Assert.Equal("", frames[1].EventId);
    }
    /// <summary>严格 UTF-8 支持初始 BOM，原生内容中的合法 Unicode 和后续 BOM 字符保持不变。</summary>
    [Fact]
    public async Task StrictUtf8_PreservesUnicodeAcrossDecoderBuffers()
    {
        var data = new string('a', 4090) + "你好𐐀\uFEFF";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("\uFEFFdata: " + data + "\n\n"));
        Assert.Equal(data, Assert.Single(await Read(stream, true)).Data);
    }
    /// <summary>非法 UTF-8 不得在严格模式下变成替代字符后当作原生 JSON 返回。</summary>
    [Fact]
    public async Task StrictUtf8_RejectsInvalidBytes()
    {
        using var stream = new MemoryStream([.. Encoding.UTF8.GetBytes("data: "), 0xff, 10, 10]);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => Read(stream, true)); Assert.True(stream.CanRead);
    }
    /// <summary>无完整空行边界的 data 事件明确失败，不能由 EOF 合成完成事件。</summary>
    [Theory] [InlineData("data: partial")] [InlineData("data: partial\n")]
    public async Task TruncatedDataEvent_FailsAtEof(string source)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(stream)); Assert.True(stream.CanRead);
    }
    /// <summary>注释或无 data 的控制字段不会变成模型消息。</summary>
    [Fact]
    public async Task MetadataOnlyEof_ReturnsNoSyntheticEvent()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(": keepalive\nid: control\nretry: 2\nevent: unused\n\n"));
        Assert.Empty(await Read(stream)); Assert.True(stream.CanRead);
    }
    /// <summary>调用者提前结束枚举也保留借用流的所有权。</summary>
    [Fact]
    public async Task EarlyConsumerExit_LeavesBorrowedStreamOpen()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: first\n\ndata: second\n\n"));
        await using (var events = SseReader.ReadAsync(stream, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        { Assert.True(await events.MoveNextAsync()); Assert.Equal("first", events.Current.Data); }
        Assert.True(stream.CanRead);
    }
    /// <summary>取消透传给实际流读取，不重连或把取消返回成正常完成。</summary>
    [Fact]
    public async Task Cancellation_RemainsFailureAndLeavesStreamOpen()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: value\n\n"));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); cancel.Cancel();
        await using var events = SseReader.ReadAsync(stream, cancel.Token).GetAsyncEnumerator(cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await events.MoveNextAsync()); Assert.True(stream.CanRead);
    }
    /// <summary>只收集帧，不解析原生 JSON 或猜测供应商终态。</summary>
    private static async Task<List<SseFrame>> Read(Stream stream, bool strict = false)
    {
        List<SseFrame> result = [];
        await foreach (var frame in SseReader.ReadAsync(stream, TestContext.Current.CancellationToken, strict)) result.Add(frame);
        return result;
    }
}
