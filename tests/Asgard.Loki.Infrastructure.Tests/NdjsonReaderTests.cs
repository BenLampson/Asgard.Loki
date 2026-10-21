using System.Text;
using Xunit;

namespace Asgard.Loki.Infrastructure.Tests;

/// <summary>通过公开字节分帧契约验证边界，不从记录内容推断模型成功。</summary>
public sealed class NdjsonReaderTests
{
    /// <summary>单字节网络边界以外的分片仍保留 Unicode、CR、空白和非法编码原始字节。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Records_PreserveRawBytesAcrossFragments(bool trailingLf)
    {
        byte[][] expected = [Encoding.UTF8.GetBytes("{\"text\":\"你好𐐀\"}\r"), [], [32, 9, 13], [0xff, 0, 0x80]];
        byte[] wire = [.. expected[0], 10, 10, .. expected[2], 10, .. expected[3], .. trailingLf ? new byte[] { 10 } : []];
        using var source = new BodyProbeStream(wire);
        var records = await Read(source, 128, 1024, wire.Length);
        Assert.Equal(expected.Length, records.Count);
        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], records[i].ToArray());
        Assert.False(source.Disposed);
    }
    /// <summary>空流不制造记录；一个 LF 只交付一个原始空记录。</summary>
    [Theory] [InlineData("", 0)] [InlineData("\n", 1)] [InlineData("\n\n", 2)] [InlineData("\r", 1)]
    public async Task EmptyAndWhitespaceRecords_AreNotInterpreted(string wire, int count)
    {
        using var source = new BodyProbeStream(Encoding.UTF8.GetBytes(wire));
        Assert.Equal(count, (await Read(source, 1, 2, wire.Length)).Count); Assert.False(source.Disposed);
    }
    /// <summary>记录快照不借用会复用的池或写入器；下一条记录不得覆写前一条。</summary>
    [Fact]
    public async Task DeliveredRecords_AreDetachedFromReusableBuffers()
    {
        byte[] first = Encoding.UTF8.GetBytes(new string('a', 17000)), second = Encoding.UTF8.GetBytes(new string('b', 18000));
        using var source = new MemoryStream([.. first, 10, .. second, 10]);
        var records = await Read(source, 18000, 40000);
        Assert.Equal(first, records[0].ToArray()); Assert.Equal(second, records[1].ToArray());
    }
    /// <summary>单记录界限包含 CR，不包含 LF，且总界限包含全部分隔符。</summary>
    [Theory] [InlineData("a\n", 1, 2)] [InlineData("a\r\n", 2, 3)] [InlineData("a", 1, 1)] [InlineData("\n\n", 1, 2)]
    public async Task ExactLimits_Succeed(string wire, int recordLimit, long totalLimit)
    {
        using var source = new BodyProbeStream(Encoding.UTF8.GetBytes(wire));
        await Read(source, recordLimit, totalLimit, totalLimit); Assert.False(source.Disposed);
    }
    /// <summary>任一实际上限越界或长度声明不符均失败，不能截断后假装正常 EOF。</summary>
    [Theory]
    [InlineData("aa\n", 1, 3, null)] [InlineData("a\r\n", 1, 3, null)]
    [InlineData("\n\n\n", 1, 2, null)] [InlineData("a\nb\n", 1, 3, null)]
    [InlineData("a\n", 1, 10, 1L)] [InlineData("a\n", 1, 10, 3L)]
    public async Task LimitsAndDeclaredLength_RejectMismatch(string wire, int recordLimit, long totalLimit, long? expected)
    {
        using var source = new BodyProbeStream(Encoding.UTF8.GetBytes(wire));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(source, recordLimit, totalLimit, expected));
        Assert.False(source.Disposed);
    }
    /// <summary>非法配置和超预算声明在首次读取之前失败，不访问流长度和位置。</summary>
    [Theory] [InlineData(0, 10, null)] [InlineData(-1, 10, null)] [InlineData(10, 0, null)]
    [InlineData(10, -1, null)] [InlineData(10, 10, -1L)] [InlineData(10, 10, 11L)]
    public async Task InvalidPolicy_FailsBeforeReading(int recordLimit, long totalLimit, long? expected)
    {
        using var source = new BodyProbeStream([1]);
        var failure = await Record.ExceptionAsync(() => Read(source, recordLimit, totalLimit, expected));
        if (expected == 11) Assert.IsType<InvalidDataException>(failure); else Assert.IsType<ArgumentOutOfRangeException>(failure, exactMatch: false);
        Assert.Equal(0, source.Position); Assert.False(source.Disposed);
    }
    /// <summary>空输入参数在枚举入口明确失败。</summary>
    [Fact]
    public async Task NullSource_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Read(null!, 10, 10));
    }
    /// <summary>预取消不读取输入，也不释放借用流。</summary>
    [Fact]
    public async Task PreCancellation_DoesNotConsumeSource()
    {
        using var source = new BodyProbeStream([1]); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(source, 10, 10, token: cancel.Token));
        Assert.Equal(0, source.Position); Assert.False(source.Disposed);
    }
    /// <summary>消费方提前退出或在已交付记录后取消，均保留输入流所有权。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task EarlyExitAndCancellation_LeaveBorrowedSourceOpen(bool cancelAfterRecord)
    {
        using var source = new BodyProbeStream(Encoding.UTF8.GetBytes("a\nb\n")); using var cancel = new CancellationTokenSource();
        await using (var records = NdjsonReader.ReadAsync(source, 10, 10, cancel.Token).GetAsyncEnumerator(cancel.Token))
        {
            Assert.True(await records.MoveNextAsync()); Assert.Equal("a", Encoding.UTF8.GetString(records.Current.Span));
            if (cancelAfterRecord) { cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await records.MoveNextAsync()); }
        }
        Assert.False(source.Disposed);
    }
    /// <summary>EOF 读取异常或 EOF 时的取消不能变成正常枚举完成。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task EofFaultOrCancellation_RemainsFailure(bool cancellation)
    {
        using var cancel = new CancellationTokenSource();
        using var source = new BodyProbeStream([97, 10]) { OnEof = cancellation ? cancel.Cancel : () => throw new IOException("fixture EOF") };
        var failure = await Record.ExceptionAsync(() => Read(source, 10, 10, token: cancel.Token));
        if (cancellation) Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure);
        Assert.False(source.Disposed);
    }
    /// <summary>没有换行的末记录已交付后，最终取消仍不能返回正常完成。</summary>
    [Fact]
    public async Task CancellationAfterFinalRecord_IsObserved()
    {
        using var cancel = new CancellationTokenSource(); using var source = new BodyProbeStream([97]);
        await using var records = NdjsonReader.ReadAsync(source, 1, long.MaxValue, cancel.Token).GetAsyncEnumerator(cancel.Token);
        Assert.True(await records.MoveNextAsync()); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await records.MoveNextAsync()); Assert.False(source.Disposed);
    }
    /// <summary>只收集原始快照，不解释 JSON 或关闭输入。</summary>
    private static async Task<List<ReadOnlyMemory<byte>>> Read(Stream source, int recordLimit, long totalLimit,
        long? expected = null, CancellationToken? token = null)
    {
        List<ReadOnlyMemory<byte>> result = [];
        await foreach (var record in NdjsonReader.ReadAsync(source, recordLimit, totalLimit, token ?? TestContext.Current.CancellationToken, expected)) result.Add(record);
        return result;
    }
}
