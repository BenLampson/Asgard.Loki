using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Google.Tests;

/// <summary>检查 Gemini 多候选终态及原生阻断结果。</summary>
public sealed class GoogleStreamTests
{
    /// <summary>重复候选结束，以及错误或阻断后的数据都不能静默接受。</summary>
    [Theory]
    [InlineData("{\"candidates\":[{\"index\":0,\"finishReason\":\"STOP\"}]}")]
    [InlineData("{\"candidates\":[{\"index\":0,\"finishReason\":\"MAX_TOKENS\"}]}")]
    [InlineData("{\"error\":{\"code\":400}}")]
    [InlineData("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}")]
    public async Task RepeatedTerminal_IsRejected(string terminal)
    {
        using var handler = Handler("data: " + terminal + "\n\ndata: " + terminal + "\n\n");
        var provider = new GoogleLlmProvider(new StubHttpClientFactory(handler));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(new(), Context(provider), TestContext.Current.CancellationToken)) { }
        });
    }

    /// <summary>候选已结束之后仍可合法返回独立用量尾块。</summary>
    [Fact]
    public async Task UsageAfterCandidateFinish_IsPreserved()
    {
        using var handler = Handler("data: {\"candidates\":[{\"index\":0,\"finishReason\":\"STOP\"}]}\n\n"
            + "data: {\"usageMetadata\":{\"totalTokenCount\":5}}\n\n");
        var provider = new GoogleLlmProvider(new StubHttpClientFactory(handler));
        List<GeminiContentEvent> events = [];
        await foreach (var item in provider.StreamAsync(new(), Context(provider), TestContext.Current.CancellationToken)) events.Add(item);
        Assert.Equal(2, events.Count);
        Assert.Equal(5, events[^1].Data.GetProperty("usageMetadata").GetProperty("totalTokenCount").GetInt32());
    }

    /// <summary>某个候选完成不能掩盖另一个候选的截断。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryObservedCandidate_RequiresTerminal(bool allFinished)
    {
        var content = "data: {\"candidates\":[{\"index\":0,\"content\":{\"parts\":[{\"text\":\"first\"}]}},{\"index\":1,\"finishReason\":\"STOP\"}]}\n\n"
            + (allFinished ? "data: {\"candidates\":[{\"index\":0,\"finishReason\":\"MAX_TOKENS\"}]}\n\n" : "");
        using var handler = Handler(content); var provider = new GoogleLlmProvider(new StubHttpClientFactory(handler));
        async Task Consume()
        {
            await foreach (var _ in provider.StreamAsync(new JsonObject(), Context(provider), TestContext.Current.CancellationToken)) { }
        }
        if (allFinished) await Consume(); else await Assert.ThrowsAsync<InvalidDataException>(Consume);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>阻断反馈原样返回，不伪造空文本和 STOP。</summary>
    [Fact]
    public async Task PromptBlock_PreservesNativeFeedback()
    {
        using var handler = Handler("data: {\"promptFeedback\":{\"blockReason\":\"SAFETY\",\"future\":false}}\n\n");
        var provider = new GoogleLlmProvider(new StubHttpClientFactory(handler));
        List<GeminiContentEvent> events = [];
        await foreach (var item in provider.StreamAsync(new JsonObject(), Context(provider), TestContext.Current.CancellationToken)) events.Add(item);
        var data = Assert.Single(events).Data;
        Assert.Equal("SAFETY", data.GetProperty("promptFeedback").GetProperty("blockReason").GetString());
        Assert.False(data.TryGetProperty("candidates", out _));
    }

    /// <summary>默认枚举值、空值和 null 错误都不是完成证据。</summary>
    [Theory]
    [InlineData("{\"error\":null}")]
    [InlineData("{\"promptFeedback\":{\"blockReason\":null}}")]
    [InlineData("{\"promptFeedback\":{\"blockReason\":\"BLOCK_REASON_UNSPECIFIED\"}}")]
    [InlineData("{\"candidates\":[{\"index\":0,\"finishReason\":\"FINISH_REASON_UNSPECIFIED\"}]}")]
    [InlineData("{\"candidates\":[{\"index\":0,\"finishReason\":\"\"}]}")]
    public async Task UnspecifiedStatus_IsNotTerminal(string data)
    {
        using var handler = Handler("data: " + data + "\n\n"); var provider = new GoogleLlmProvider(new StubHttpClientFactory(handler));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(new(), Context(provider), TestContext.Current.CancellationToken)) { }
        });
    }

    /// <summary>终态后的新增文本不能被当成合法已完成流。</summary>
    [Fact]
    public async Task ContentAfterFinish_IsRejected()
    {
        using var handler = Handler("data: {\"candidates\":[{\"index\":0,\"finishReason\":\"STOP\"}]}\n\n"
            + "data: {\"candidates\":[{\"index\":0,\"content\":{\"parts\":[{\"text\":\"late\"}]}}]}\n\n");
        var provider = new GoogleLlmProvider(new StubHttpClientFactory(handler));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(new(), Context(provider), TestContext.Current.CancellationToken)) { }
        });
    }

    /// <summary>为测试提供原生 SSE 响应。</summary>
    private static StubHttpHandler Handler(string data) => new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") }));
    /// <summary>使用已确定的完整目标和独立测试凭据。</summary>
    private static LlmAdapterContext Context(GoogleLlmProvider provider) => new(new LlmTarget
    {
        Id = "target", ProviderId = provider.Id, BaseAddress = new("https://google.invalid/v1beta/"),
        UpstreamModel = "model", CredentialId = "key", Protocols = LlmProtocol.Gemini
    }, new("key", "test-only"), "public", "session");
}
