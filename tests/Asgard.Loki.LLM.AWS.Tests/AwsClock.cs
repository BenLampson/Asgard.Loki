namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>显式确定性签名/到期时钟，不读取 AWS 服务时间。</summary>
internal sealed class AwsClock : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = new(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);
    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Now;
}
