using System.Text;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>choice/tool 原始索引、身份与原生结束条件不能被 DONE 或 EOF 掩盖。</summary>
public sealed class MaaSStreamStateTests
{
    /// <summary>原生截断、重复/重开、错关联和身份改变都不能释放暂存终态。</summary>
    [Theory] [InlineData("missing-finish")] [InlineData("missing-done")] [InlineData("after-done")] [InlineData("duplicate-finish")] [InlineData("reopen")] [InlineData("missing-choice")] [InlineData("duplicate-choice")] [InlineData("negative-index")] [InlineData("fraction-index")] [InlineData("quoted-index")] [InlineData("rounded-index")] [InlineData("primitive-delta")] [InlineData("id-change")] [InlineData("model-change")] [InlineData("tool-id-change")] [InlineData("tool-id-reuse")] [InlineData("tool-missing-id")] [InlineData("duplicate-tool")] [InlineData("tool-index")] [InlineData("primitive-tools")]
    public async Task InvalidNativeLifecycle_Fails(string scenario)
    {
        var start = MaaSStreamFixture.Frame(MaaSStreamFixture.Start); var finish = MaaSStreamFixture.Frame(MaaSStreamFixture.Finish); var done = MaaSStreamFixture.Frame("[DONE]");
        var badChoice = scenario switch { "duplicate-choice" => "{\"choices\":[{\"index\":0,\"delta\":{}},{\"index\":0,\"delta\":{}}]}", "negative-index" => "{\"choices\":[{\"index\":-1}]}", "fraction-index" => "{\"choices\":[{\"index\":0.1}]}", "quoted-index" => "{\"choices\":[{\"index\":\"0\"}]}", "rounded-index" => "{\"choices\":[{\"index\":1.00000000000000001}]}", "primitive-delta" => "{\"choices\":[{\"index\":0,\"delta\":false}]}", _ => null };
        var toolStart = MaaSStreamFixture.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":2,\"id\":\"a\",\"type\":\"function\",\"function\":{\"name\":\"lookup\",\"arguments\":\"\"}}]}}]}");
        var toolBad = scenario switch { "tool-id-change" => "[{\"index\":2,\"id\":\"b\"}]", "tool-id-reuse" => "[{\"index\":3,\"id\":\"a\"}]", "tool-missing-id" => "[{\"index\":3,\"function\":{\"arguments\":\"{}\"}}]", "duplicate-tool" => "[{\"index\":2},{\"index\":2}]", "tool-index" => "[{\"index\":0.1}]", "primitive-tools" => "false", _ => null };
        var text = scenario switch
        {
            "missing-finish" => start + done, "missing-done" => start + finish, "after-done" => start + finish + done + MaaSStreamFixture.Frame("{\"future\":true}"), "duplicate-finish" => start + finish + finish + done, "reopen" => start + finish + start + done, "missing-choice" => start + MaaSStreamFixture.Frame("{\"choices\":[{\"index\":1,\"finish_reason\":\"stop\"}]}") + done,
            "id-change" => MaaSStreamFixture.Frame("{\"id\":\"a\",\"choices\":[]}") + start + finish + MaaSStreamFixture.Frame("{\"id\":\"b\",\"choices\":[]}") + done,
            "model-change" => MaaSStreamFixture.Frame("{\"model\":\"a\",\"choices\":[]}") + start + finish + MaaSStreamFixture.Frame("{\"model\":\"b\"}") + done,
            _ when badChoice is not null => start + MaaSStreamFixture.Frame(badChoice) + finish + done,
            _ => start + toolStart + MaaSStreamFixture.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":" + toolBad + "}}]}") + finish + done
        };
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(text)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(body))); var factory = new VertexHttpFactory(handler); var successfulFinish = false; await Assert.ThrowsAsync<InvalidDataException>(() => MaaSStreamFixture.Consume(new(factory, source, timeProvider: clock), TestContext.Current.CancellationToken, item => { if (item.Data.TryGetProperty("choices", out var choices)) successfulFinish |= choices.EnumerateArray().Any(choice => choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == System.Text.Json.JsonValueKind.String); })); Assert.False(successfulFinish); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>多 choice 可以按稀疏索引交错结束，终态都等待整体来源完成。</summary>
    [Theory] [InlineData("2147483647")] [InlineData("1e2")] [InlineData("1.00")]
    public async Task SparseExactChoiceIndices_KeepNativeValues(string index)
    {
        var text = MaaSStreamFixture.Frame(MaaSStreamFixture.Start) + MaaSStreamFixture.Frame("{\"choices\":[{\"index\":" + index + ",\"delta\":{\"content\":\"second\"},\"finish_reason\":\"future_reason\"}]}") + MaaSStreamFixture.Frame(MaaSStreamFixture.Finish) + MaaSStreamFixture.Frame("[DONE]"); var clock = new VertexClock(); var source = new VertexTokenSource(clock); var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(text)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(body))); var factory = new VertexHttpFactory(handler); var count = 0; await MaaSStreamFixture.Consume(new(factory, source, timeProvider: clock), TestContext.Current.CancellationToken, item => { if (count == 1) Assert.Equal(index, item.Data.GetProperty("choices")[0].GetProperty("index").GetRawText()); Assert.Equal(count != 0, body.Disposed); count++; }); Assert.Equal(3, count);
    }
}
