namespace Asgard.Loki.LLM.Tencent.Tests;

/// <summary>签名向量和到期验证使用固定逻辑时钟，不等待真实时间。</summary>
internal sealed class HunyuanClock : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1792454400);
    public override DateTimeOffset GetUtcNow() => Now;
}
