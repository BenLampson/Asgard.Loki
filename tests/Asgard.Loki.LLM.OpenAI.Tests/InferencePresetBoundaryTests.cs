using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;
using static Asgard.Loki.LLM.OpenAI.Tests.InferencePresetFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>推理服务预设原生结束和读取关闭边界，不在客户端创建后台流程。</summary>
public sealed class InferencePresetBoundaryTests
{
    /// <summary>没有实际 DONE 或 Responses 原生终态，不能返回结束帧。</summary>
    [Theory] [InlineData("chat")] [InlineData("responses")] [InlineData("text")]
    public async Task MissingTerminal_DiscardsEnding(string mode)
    {
        var source = mode == "responses" ? string.Concat(Part(mode).GetProperty("events").EnumerateArray().Where(e => !e.TryGetProperty("type", out var type) || type.GetString() != "response.completed").Select(InferencePresetFixture.Frame)) : Part(mode).GetProperty("sse").GetString()!.Replace("data: [DONE]\n\n", ""); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = Factory(handler, mode); await using var sdk = Build(factory, mode); List<LlmJsonEvent> seen = []; await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, mode, true, seen)); Assert.DoesNotContain(seen, Ending);
    }
    /// <summary>重复实际控制终态和结束后追加已知生成都拒绝，不泄露缓冲结束。</summary>
    [Theory] [InlineData("chat", false)] [InlineData("chat", true)] [InlineData("responses", false)] [InlineData("responses", true)] [InlineData("text", false)] [InlineData("text", true)]
    public async Task PostTerminal_IsRejected(string mode, bool duplicate)
    {
        var after = mode == "responses" ? duplicate ? JsonSerializer.Serialize(Part(mode).GetProperty("events").EnumerateArray().Single(e => e.TryGetProperty("type", out var type) && type.GetString() == "response.completed")) : "{\"type\":\"response.output_text.delta\",\"delta\":\"late\"}" : duplicate ? "[DONE]" : "{\"choices\":[{\"index\":0,\"text\":\"late\"}]}";
        var source = Part(mode).GetProperty("sse").GetString()!+OpenAiHttpFixture.Frame(after); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = Factory(handler, mode); await using var sdk = Build(factory, mode); List<LlmJsonEvent> seen = []; await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, mode, true, seen)); Assert.DoesNotContain(seen, Ending);
    }
    /// <summary>三个原生生成操作分别覆盖完整 JSON 和 SSE。</summary>
    public static IEnumerable<object[]> LateCases() { foreach (var mode in Modes) foreach (var stream in new[] { false, true }) foreach (var cancel in new[] { false, true }) yield return [mode, stream, cancel]; }
    /// <summary>读取到末尾才失败或客户端关闭时取消，不可返回 JSON 窗口或流结束。</summary>
    [Theory] [MemberData(nameof(LateCases))]
    public async Task LateFailure_PreventsDelivery(string mode, bool stream, bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new OllamaBodyStream(Bytes(stream ? Part(mode).GetProperty("sse").GetString()! : Part(mode).GetProperty("result").GetRawText())) { FailAtEof = !cancel }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, stream))); var factory = new OpenAiHttpFactory(handler) { OnDispose = () => { if (cancel) cancellation.Cancel(); } }; await using var sdk = Build(factory, mode); List<LlmJsonEvent> seen = [];
        var failure = await Record.ExceptionAsync(() => Invoke(sdk, mode, stream, seen, cancellationToken: cancellation.Token)); Assert.NotNull(failure); if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
