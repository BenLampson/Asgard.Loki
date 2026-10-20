namespace Asgard.Loki.Video.Volcengine.Tests;

/// <summary>显式固定签名时间，单元测试不依赖系统时钟或等待。</summary>
internal sealed class AssetTestClock : TimeProvider
{
    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => new(2026, 10, 20, 1, 2, 3, TimeSpan.Zero);
}
