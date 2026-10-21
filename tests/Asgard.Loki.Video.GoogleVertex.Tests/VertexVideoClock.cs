namespace Asgard.Loki.Video.GoogleVertex.Tests;

/// <summary>确定性到期测试时钟，不等待真实时间。</summary>
internal sealed class VertexVideoClock : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Now;
}
