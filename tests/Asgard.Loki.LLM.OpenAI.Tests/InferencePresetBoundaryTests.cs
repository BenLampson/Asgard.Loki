using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;
using static Asgard.Loki.LLM.OpenAI.Tests.InferencePresetFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>兼容配置只验证接线；完整原生终态矩阵由同包基础协议测试负责。</summary>
public sealed class InferencePresetBoundaryTests
{
    /// <summary>代理配置仍沿用原生 Chat 的 EOF 失败规则，不提前交付结束帧。</summary>
    [Fact]
    public async Task LateEofFailure_DiscardsEnding()
    {
        const string mode = "chat";
        var body = new OllamaBodyStream(Bytes(Part(mode).GetProperty("sse").GetString()!)) { FailAtEof = true };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true)));
        var factory = Factory(handler, mode); await using var sdk = Build(factory, mode, profile: 1);
        List<LlmJsonEvent> seen = [];
        await Assert.ThrowsAsync<IOException>(() => Invoke(sdk, mode, true, seen));
        Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed);
        Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
