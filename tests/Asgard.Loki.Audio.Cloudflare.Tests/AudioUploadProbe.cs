namespace Asgard.Loki.Audio.Cloudflare.Tests;

/// <summary>不支持 Length/Position 的碎片化借用输入，观察消费及关闭边界。</summary>
internal sealed class AudioUploadProbe(byte[] bytes) : MemoryStream(bytes)
{
    internal int ChunkSize { get; init; } = 5;
    internal Action? OnEof { get; init; }
    internal long Consumed => base.Position;
    internal bool Disposed { get; private set; }
    public override bool CanSeek => false;
    public override long Length => throw new InvalidOperationException("length inspection forbidden");
    public override long Position { get => throw new InvalidOperationException("position inspection forbidden"); set => throw new InvalidOperationException("rewind forbidden"); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var count = base.Read(buffer.Span[..Math.Min(buffer.Length, ChunkSize)]);
        if (count == 0) OnEof?.Invoke(); return ValueTask.FromResult(count);
    }
    protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
}
