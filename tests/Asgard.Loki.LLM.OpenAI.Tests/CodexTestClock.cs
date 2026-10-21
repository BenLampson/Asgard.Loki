namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>测试时钟只由用例推进，不使用等待或真实令牌时间。</summary>
internal sealed class CodexTestClock : TimeProvider
{
    internal DateTimeOffset Current { get; set; } = CodexFixture.Now;
    public override DateTimeOffset GetUtcNow() => Current;
}
