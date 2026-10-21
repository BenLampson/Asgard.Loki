namespace Asgard.Loki.Video.Kling.Tests;

/// <summary>可控 JWT 签名与发送时间，不等待真实时钟。</summary>
internal sealed class KlingTestClock : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = new(2026, 10, 20, 1, 2, 3, TimeSpan.Zero);
    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Now;
}
