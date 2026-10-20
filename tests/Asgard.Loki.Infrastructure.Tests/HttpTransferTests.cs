using System.Text;
using Xunit;

namespace Asgard.Loki.Infrastructure.Tests;

/// <summary>共享流复制及上传的一次性发送、字节预算与借用资源契约。</summary>
public sealed class HttpTransferTests
{
    /// <summary>有界复制保持二进制字节及目标现有前缀，只释放 HTTP 内容读取流。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Copy_PreservesBinaryAndBorrowedDestination(bool declared)
    {
        byte[] bytes = [0xff, 0, 1, 2, 3, 4]; var source = new BodyProbeStream(bytes); using var content = new StreamContent(source);
        if (declared) content.Headers.ContentLength = bytes.Length;
        using var destination = new MemoryStream(); destination.WriteByte(9);
        var count = await HttpBodyReader.CopyToAsync(content, destination, bytes.Length, TestContext.Current.CancellationToken);
        Assert.Equal(bytes.Length, count); Assert.Equal(new byte[] { 9, 0xff, 0, 1, 2, 3, 4 }, destination.ToArray()); Assert.True(source.Disposed); Assert.True(destination.CanWrite);
    }
    /// <summary>声明不符或读取超限失败时，目标保留已写入的有限前缀并继续由调用者控制。</summary>
    [Theory] [InlineData("short")] [InlineData("long")] [InlineData("limit")]
    public async Task Copy_LengthOrLimitFailure_DoesNotPublishCompleteResult(string kind)
    {
        var source = new BodyProbeStream([1, 2, 3, 4, 5, 6]); using var content = new StreamContent(source); using var destination = new MemoryStream();
        if (kind != "limit") content.Headers.ContentLength = kind == "short" ? 4 : 7;
        await Assert.ThrowsAsync<InvalidDataException>(() => HttpBodyReader.CopyToAsync(content, destination, kind == "limit" ? 4 : 10, TestContext.Current.CancellationToken));
        Assert.True(source.Disposed); Assert.True(destination.CanWrite); Assert.InRange(destination.Length, 1, 6);
    }
    /// <summary>最终 EOF、释放故障或释放时取消均阻止内容复制完成。</summary>
    [Theory] [InlineData("eof")] [InlineData("dispose")] [InlineData("cancel")]
    public async Task Copy_FinalFaultAndCancellation_Propagate(string kind)
    {
        using var cancellation = new CancellationTokenSource();
        var source = new BodyProbeStream([1]) { OnEof = kind == "eof" ? () => throw new IOException("fixture EOF") : null,
            OnDispose = kind == "dispose" ? () => throw new IOException("fixture disposal") : kind == "cancel" ? cancellation.Cancel : null };
        using var content = new StreamContent(source); using var destination = new MemoryStream();
        if (kind == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HttpBodyReader.CopyToAsync(content, destination, 10, cancellation.Token));
        else await Assert.ThrowsAsync<IOException>(() => HttpBodyReader.CopyToAsync(content, destination, 10, TestContext.Current.CancellationToken));
        Assert.True(source.Disposed); Assert.True(destination.CanWrite);
    }
    /// <summary>上传只读取剩余字节，不检查 Length/Position，不关闭输入或输出且拒绝重复序列化。</summary>
    [Fact]
    public async Task BorrowedUpload_SendsOnceWithoutInspectingOrOwningStream()
    {
        using var source = new UploadProbeStream([1, 2, 3, 4]); var scratch = new byte[1]; await source.ReadExactlyAsync(scratch, TestContext.Current.CancellationToken);
        using var destination = new MemoryStream();
        using (var content = new BorrowedStreamContent(source))
        {
            Assert.Null(content.Headers.ContentLength); await content.CopyToAsync(destination, TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 2, 3, 4 }, destination.ToArray());
            await Assert.ThrowsAsync<InvalidOperationException>(() => content.CopyToAsync(destination, TestContext.Current.CancellationToken));
        }
        Assert.False(source.Disposed); Assert.True(destination.CanWrite); Assert.Equal(4, source.Consumed);
    }
    /// <summary>读取最后边界故障或取消后不能重发输入，源流仍保持借用关系。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task BorrowedUpload_FailureDoesNotCloseOrReplaySource(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        using var source = new UploadProbeStream([1, 2]) { OnEof = () => { if (cancel) cancellation.Cancel(); else throw new IOException("fixture EOF"); } };
        using var content = new BorrowedStreamContent(source); using var destination = new MemoryStream();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => content.CopyToAsync(destination, cancellation.Token));
        else Assert.IsType<IOException>((await Assert.ThrowsAsync<HttpRequestException>(() => content.CopyToAsync(destination, TestContext.Current.CancellationToken))).InnerException);
        Assert.False(source.Disposed); await Assert.ThrowsAsync<InvalidOperationException>(() => content.CopyToAsync(destination, TestContext.Current.CancellationToken));
    }
    /// <summary>multipart 总预算包含 framing 和所有字段，不仅是文件字节。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task BoundedMultipart_CountsEntireWireAndBorrowsFile(bool limited)
    {
        using var source = new UploadProbeStream([0xff, 0, 1]); var form = new MultipartFormDataContent("fixture-boundary");
        form.Add(new StringContent("original text", Encoding.UTF8), "prompt"); form.Add(new BorrowedStreamContent(source), "input_reference", "frame.png");
        using var content = new BoundedHttpContent(form, limited ? 10 : 10000); using var destination = new MemoryStream();
        if (limited) await Assert.ThrowsAsync<InvalidDataException>(() => content.CopyToAsync(destination, TestContext.Current.CancellationToken));
        else
        {
            await content.CopyToAsync(destination, TestContext.Current.CancellationToken); Assert.Contains("original text", Encoding.UTF8.GetString(destination.ToArray()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => content.CopyToAsync(destination, TestContext.Current.CancellationToken));
        }
        Assert.False(source.Disposed); Assert.True(destination.CanWrite); if (limited) Assert.InRange(destination.Length, 0, 10);
    }
    /// <summary>已知超限正文在取得包装所有权前拒绝，不消费输入或创建传输。</summary>
    [Fact]
    public void DeclaredUploadLimit_IsCheckedBeforeSending()
    {
        using var inner = new ByteArrayContent([1, 2, 3]); Assert.Throws<InvalidDataException>(() => new BoundedHttpContent(inner, 2));
    }
}
