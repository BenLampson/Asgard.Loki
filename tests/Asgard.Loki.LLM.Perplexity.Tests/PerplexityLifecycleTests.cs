using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Perplexity.Tests.PerplexityFixture;

namespace Asgard.Loki.LLM.Perplexity.Tests;

/// <summary>原生生命周期与未知业务事件分离，控制标记不能替代语义终态。</summary>
public sealed class PerplexityLifecycleTests
{
    /// <summary>每个已知原生事件在终态后都必须拒绝，不能继续生成。</summary>
    public static IEnumerable<object[]> KnownEvents() => new[] { "response.created", "response.in_progress", "response.completed", "response.failed", "response.output_item.added", "response.output_item.done", "response.output_text.delta", "response.output_text.done", "response.reasoning.started", "response.reasoning.stopped", "response.reasoning.search_queries", "response.reasoning.search_results", "response.reasoning.image_search_queries", "response.reasoning.image_search_results", "response.reasoning.fetch_url_queries", "response.reasoning.fetch_url_results" }.Select(type => new object[] { type });
    /// <summary>所有已知生命周期事件遵循相同终态边界。</summary>
    [Theory] [MemberData(nameof(KnownEvents))]
    public async Task KnownAfterTerminal_IsRejected(string type)
    { await Reject(Frame(Golden.GetProperty("partial_completed"))+Frame(JsonSerializer.SerializeToElement(new { type, sequence_number = 1 }))); }
    /// <summary>相似前缀、业务 ID/error/sequence 不被误认为原生连接状态。</summary>
    [Theory] [InlineData("vendor.trace")] [InlineData("response.reasoning.trace")] [InlineData("response.search.business")]
    public async Task UnknownAfterTerminal_IsOriginal(string type)
    {
        var data = JsonSerializer.SerializeToElement(new { type, id = "business-ID", response_id = "business-response", error = new { message = "business" }, sequence_number = -999 }); var source = Frame(Golden.GetProperty("partial_completed"))+Frame(data);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(source)), true))); var factory = new PerplexityHttpFactory(handler); List<ResponseEvent> seen = []; await Drain(new(factory), TestContext.Current.CancellationToken, seen); Assert.Equal(2, seen.Count); Assert.True(JsonElement.DeepEquals(data, seen[1].Data));
    }
    /// <summary>终态可带单个控制标记，EOF 仍是提交尾部的边界。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeTerminal_OptionalDone(bool done)
    {
        var source = Frame(Golden.GetProperty("sparse_failed"))+(done ? "data: [DONE]\n\n" : ""); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(source)), true))); var factory = new PerplexityHttpFactory(handler); List<ResponseEvent> seen = []; await Drain(new(factory), TestContext.Current.CancellationToken, seen); Assert.Equal("response.failed", Assert.Single(seen).Type);
    }
    /// <summary>缺失/重复终态、无效关联、无效序号与矛盾状态都不能交付结束。</summary>
    [Theory] [InlineData("empty")] [InlineData("no-terminal")] [InlineData("early-done")] [InlineData("repeat-done")] [InlineData("after-done")] [InlineData("sequence-missing")] [InlineData("sequence-float")] [InlineData("sequence-repeat")] [InlineData("sequence-backward")] [InlineData("id-drift")] [InlineData("id-empty")] [InlineData("failed-error")] [InlineData("failed-message")] [InlineData("event-mismatch")] [InlineData("in-progress-terminal")] [InlineData("queued-terminal")] [InlineData("response-null")]
    public async Task InvalidNativeLifecycle_DiscardsEnding(string fault)
    {
        var created = Frame(Golden.GetProperty("events")[0]); var terminal = Frame(Golden.GetProperty("partial_completed"));
        var source = fault switch
        {
            "empty" => "", "no-terminal" => created, "early-done" => "data: [DONE]\n\n", "repeat-done" => terminal+"data: [DONE]\n\ndata: [DONE]\n\n", "after-done" => terminal+"data: [DONE]\n\ndata: {\"type\":\"vendor.trace\"}\n\n",
            "sequence-missing" => "data: {\"type\":\"response.completed\"}\n\n", "sequence-float" => "data: {\"type\":\"response.completed\",\"sequence_number\":1.5}\n\n",
            "sequence-repeat" => created+terminal, "sequence-backward" => Frame(Golden.GetProperty("events")[1])+terminal,
            "id-drift" => created+"data: {\"type\":\"response.completed\",\"sequence_number\":1,\"response\":{\"id\":\"other\"}}\n\n", "id-empty" => "data: {\"type\":\"response.completed\",\"sequence_number\":0,\"response\":{\"id\":\"\"}}\n\n",
            "failed-error" => "data: {\"type\":\"response.failed\",\"sequence_number\":0}\n\n", "failed-message" => "data: {\"type\":\"response.failed\",\"sequence_number\":0,\"error\":{}}\n\n",
            "event-mismatch" => "event: response.failed\n"+terminal,
            "in-progress-terminal" => "data: {\"type\":\"response.completed\",\"sequence_number\":0,\"response\":{\"status\":\"in_progress\"}}\n\n", "queued-terminal" => "data: {\"type\":\"response.completed\",\"sequence_number\":0,\"response\":{\"status\":\"queued\"}}\n\n",
            _ => "data: {\"type\":\"response.completed\",\"sequence_number\":0,\"response\":null}\n\n"
        }; await Reject(source);
    }
    /// <summary>原生稀疏 error 仅要求 message，不强加其他供应商的 code 或 response 结构。</summary>
    [Fact]
    public async Task MessageOnlyFailure_IsValid()
    {
        var source = "data: {\"type\":\"response.failed\",\"sequence_number\":9223372036854775807,\"error\":{\"message\":\"\"}}\n\n"; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(source)), true))); var factory = new PerplexityHttpFactory(handler); List<ResponseEvent> seen = []; await Drain(new(factory), TestContext.Current.CancellationToken, seen); Assert.Single(seen);
    }
    /// <summary>已暂存终态后的连接错误保留原始错误字节并撤回终态。</summary>
    [Fact]
    public async Task LateConnectionError_DiscardsEnding()
    {
        var error = JsonSerializer.Deserialize<JsonElement>("{\"type\":\"error\",\"error\":{\"message\":\"late\"}}"); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(Frame(Golden.GetProperty("partial_completed"))+Frame(error))), true))); var factory = new PerplexityHttpFactory(handler); List<ResponseEvent> seen = [];
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Drain(new(factory), TestContext.Current.CancellationToken, seen)); Assert.Empty(seen); Assert.Equal(Bytes(JsonSerializer.Serialize(error)), failure.Body.ToArray());
    }
    /// <summary>尾部数量上限也约束终态之后的未知原生业务元数据。</summary>
    [Fact]
    public async Task TailLimit_DiscardsEnding()
    { await Reject(Frame(Golden.GetProperty("partial_completed"))+"data: {\"type\":\"vendor.trace\"}\n\n", new() { MaximumBufferedTerminalEvents = 1 }); }
    /// <summary>会覆盖实际模型或隐式回退的根选择器在工厂创建前拒绝，嵌套业务字段不受影响。</summary>
    [Theory] [InlineData("models")] [InlineData("Models")] [InlineData("preset")] [InlineData("PRESET")] [InlineData("profile")] [InlineData("Profile")]
    public async Task AlternateModelSelector_PrecedesFactory(string selector)
    {
        var payload = Payload(); payload[selector] = null; using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new PerplexityHttpFactory(handler); var provider = new PerplexityResponsesProvider(factory);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CreateAsync(payload, Context(provider), TestContext.Current.CancellationToken)); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    private static async Task Reject(string source, PerplexityResponsesOptions? options = null)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(source)), true))); var factory = new PerplexityHttpFactory(handler); List<ResponseEvent> seen = []; await Assert.ThrowsAsync<InvalidDataException>(() => Drain(new(factory, options), TestContext.Current.CancellationToken, seen)); Assert.DoesNotContain(seen, Ending); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
