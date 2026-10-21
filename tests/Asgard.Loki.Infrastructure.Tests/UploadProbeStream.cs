namespace Asgard.Loki.Infrastructure.Tests;

/// <summary>验证上传不检查长度或位置、保持当前位置且不取得释放权的独立流探针。</summary>
internal sealed class UploadProbeStream(byte[] bytes) : MemoryStream(bytes)
{
    /// <summary>最终读取边界可抛错或取消。</summary>
    internal Action? OnEof { get; init; }
    /// <summary>已消费字节数，仅供测试读取。</summary>
    internal long Consumed => base.Position;
    /// <summary>记录输入是否被错误关闭。</summary>
    internal bool Disposed { get; private set; }
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override long Length => throw new InvalidOperationException("length inspection forbidden");
    /// <inheritdoc />
    public override long Position { get => throw new InvalidOperationException("position inspection forbidden"); set => throw new InvalidOperationException("rewind forbidden"); }
    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var count = base.Read(buffer.Span[..Math.Min(buffer.Length, 3)]);
        if (count == 0) OnEof?.Invoke(); return ValueTask.FromResult(count);
    }
    /// <inheritdoc />
    protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
}
