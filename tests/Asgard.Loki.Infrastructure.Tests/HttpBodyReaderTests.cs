using Xunit;

namespace Asgard.Loki.Infrastructure.Tests;

/// <summary>通过公开基础设施契约验证字节读取、显式上限、EOF 和释放，不解释任何模型结果。</summary>
public sealed class HttpBodyReaderTests
{
    /// <summary>任意二进制及空正文保持完整；允许无声明长度或恰好达到上限。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task CompleteBody_PreservesBytesAndDisposesReadStream(bool declared, bool empty)
    {
        byte[] expected = empty ? [] : [0xff, 0x00, 0xf0, 0x80, 10, 13, 0x01, 0x7f]; var stream = new BodyProbeStream(expected);
        using var content = new StreamContent(stream); if (declared) content.Headers.ContentLength = expected.Length;
        var actual = await HttpBodyReader.ReadAsync(content, Math.Max(1, expected.Length), TestContext.Current.CancellationToken);
        Assert.Equal(expected, actual); Assert.True(stream.Disposed);
    }
    /// <summary>声明过短、声明过长、声明超限或读取超限都失败，不截取后返回看似完整数据。</summary>
    [Theory] [InlineData("short")] [InlineData("long")] [InlineData("declared-limit")] [InlineData("read-limit")]
    public async Task LengthAndLimitMismatch_FailsExplicitly(string kind)
    {
        var stream = new BodyProbeStream([1, 2, 3, 4]); using var content = new StreamContent(stream);
        if (kind != "read-limit") content.Headers.ContentLength = kind == "short" ? 3 : kind == "long" ? 5 : 100;
        await Assert.ThrowsAsync<InvalidDataException>(() => HttpBodyReader.ReadAsync(content, kind == "read-limit" ? 3 : 10, TestContext.Current.CancellationToken));
        Assert.Equal(kind != "declared-limit", stream.Disposed);
    }
    /// <summary>EOF 和释放故障不能在正文已经读取后变成成功。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LateFault_PreventsBodyDelivery(bool dispose)
    {
        var stream = new BodyProbeStream([1]) { OnEof = dispose ? null : () => throw new IOException("fixture EOF"), OnDispose = dispose ? () => throw new IOException("fixture disposal") : null };
        using var content = new StreamContent(stream);
        await Assert.ThrowsAsync<IOException>(() => HttpBodyReader.ReadAsync(content, 10, TestContext.Current.CancellationToken)); Assert.True(stream.Disposed);
    }
    /// <summary>取消可在 EOF 或释放时发生，最后一个检查仍阻止交付结果。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LateCancellation_PreventsBodyDelivery(bool dispose)
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new BodyProbeStream([1]) { OnEof = dispose ? null : cancellation.Cancel, OnDispose = dispose ? cancellation.Cancel : null };
        using var content = new StreamContent(stream);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HttpBodyReader.ReadAsync(content, 10, cancellation.Token)); Assert.True(stream.Disposed);
    }
    /// <summary>无效上限与预取消都不得打开内容流或消费调用者输入。</summary>
    [Fact]
    public async Task InvalidLimitAndPreCancellation_DoNotConsumeContent()
    {
        var stream = new BodyProbeStream([1]); using var content = new StreamContent(stream);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => HttpBodyReader.ReadAsync(content, 0, TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HttpBodyReader.ReadAsync(content, 10, cancellation.Token));
        Assert.False(stream.Disposed); Assert.Equal(0, stream.Position);
    }
}
