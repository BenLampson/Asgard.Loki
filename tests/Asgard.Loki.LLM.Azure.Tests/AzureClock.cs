namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>确定性认证时钟，不使用真实等待或延迟制造过期竞态。</summary>
internal sealed class AzureClock : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Now;
}
