namespace Asgard.Loki.Audio.Tests;

/// <summary>禁止查询长度/位置与回卷的借用上传，观察核心是否错误读取或关闭。</summary>
internal sealed class ObservedAudioUpload(byte[] bytes) : MemoryStream(bytes)
{
    internal int Reads { get; private set; }
    internal bool Disposed { get; private set; }
    public override bool CanSeek => false;
    public override long Length => throw new InvalidOperationException("Core must not query upload length");
    public override long Position { get => throw new InvalidOperationException("Core must not query upload position"); set => throw new InvalidOperationException("Core must not rewind upload"); }
    public override int Read(byte[] buffer, int offset, int count) { Reads++; return base.Read(buffer, offset, count); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { Reads++; return base.ReadAsync(buffer, cancellationToken); }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
