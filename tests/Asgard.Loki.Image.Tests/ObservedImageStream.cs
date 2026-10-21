namespace Asgard.Loki.Image.Tests;

/// <summary>不允许核心查询长度或位置的借用图片流，观察预读、回卷和错误释放。</summary>
internal sealed class ObservedImageStream(byte[] bytes) : MemoryStream(bytes)
{
    internal int Reads { get; private set; }
    internal bool Disposed { get; private set; }
    public override bool CanSeek => false;
    public override long Length => throw new InvalidOperationException("Must not inspect image length");
    public override long Position { get => throw new InvalidOperationException("Must not inspect image position"); set => throw new InvalidOperationException("Must not rewind image"); }
    public override int Read(byte[] buffer, int offset, int count) { Reads++; return base.Read(buffer, offset, count); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { Reads++; return base.ReadAsync(buffer, cancellationToken); }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
