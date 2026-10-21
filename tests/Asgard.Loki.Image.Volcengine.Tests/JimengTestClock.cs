namespace Asgard.Loki.Image.Volcengine.Tests;

/// <summary>签名样本的确定性时钟，不读取机器当前时间。</summary>
internal sealed class JimengTestClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 10, 20, 1, 2, 3, TimeSpan.Zero);
}
