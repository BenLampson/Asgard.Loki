using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Anthropic.Tests.AnthropicHttpFixture;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>兼容配置复用原生 Messages；这里只保留一次外层取消接线回归。</summary>
public sealed class InferencePresetMessagesBoundaryTests
{
    /// <summary>Bearer 预设的客户端关闭时取消，仍撤回原生终态。</summary>
    [Fact]
    public async Task ClientDisposalCancellation_DiscardsEnding()
    {
        var window = Golden("inference-presets"); var profile = window.GetProperty("profiles")[1];
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var body = new OllamaBodyStream(Bytes(window.GetProperty("sse").GetString()!));
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true)));
        var factory = new AnthropicHttpFactory(handler) { OnDispose = cancellation.Cancel };
        var provider = new AnthropicLlmProvider(factory, new AnthropicHttpOptions { Authentication = AnthropicAuthentication.Bearer });
        var target = Target(provider, profile.GetProperty("root").GetString()!, window.GetProperty("model").GetString()!);
        await using var sdk = Build(provider, target: target, key: profile.GetProperty("key").GetString()!);
        List<ClaudeMessageEvent> seen = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in sdk.LLM.ClaudeMessages.StreamAsync(
                window.GetProperty("caller").Deserialize<ClaudeMessageRequest>()!, new() { SessionId = "preset-cancellation" }, cancellation.Token)) seen.Add(item);
        });
        Assert.DoesNotContain(seen, e => e.Type is "message_delta" or "message_stop");
        Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
