namespace Asgard.Loki.Image.XAI.Tests;

/// <summary>观察被拒绝 multipart 输入的所有权，不能被适配器读取或释放。</summary>
internal sealed class XaiBorrowedStream : MemoryStream
{
    internal int Reads { get; private set; }
    internal bool Disposed { get; private set; }
    public override int Read(byte[] buffer, int offset, int count) { Reads++; return base.Read(buffer, offset, count); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { Reads++; return base.ReadAsync(buffer, cancellationToken); }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
