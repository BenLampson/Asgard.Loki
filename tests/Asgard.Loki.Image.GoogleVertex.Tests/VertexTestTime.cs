namespace Asgard.Loki.Image.GoogleVertex.Tests;

/// <summary>离线认证有效期时钟，不等待真实时间。</summary>
internal sealed class VertexTestTime : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
