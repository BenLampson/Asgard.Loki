using System.Net;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Perplexity.Tests;

/// <summary>显式原生 SSE error 不要求 JSON，错误正文不得被误当 DONE 或业务元数据。</summary>
public sealed class PerplexityConnectionErrorTests
{
    /// <summary>普通文本和看似控制标记的错误均保留原始载荷，晚期错误撤回已有终态。</summary>
    [Theory] [InlineData(false, false, false)] [InlineData(false, false, true)] [InlineData(false, true, false)] [InlineData(false, true, true)] [InlineData(true, false, false)] [InlineData(true, false, true)] [InlineData(true, true, false)] [InlineData(true, true, true)]
    public async Task ExplicitError_PreservesBytes(bool sonar, bool late, bool marker)
    {
        var data = marker ? "[DONE]" : "Upstream temporarily unavailable: 原始错误"; var prefix = late ? (sonar ? SonarFixture.Golden : PerplexityFixture.Golden).GetProperty("literal_sse").GetString()! : "";
        var body = new OllamaBodyStream(PerplexityFixture.Bytes(prefix+"event: error\ndata: "+data+"\n\n")); using var handler = new StubHttpHandler((_, _) => Task.FromResult(PerplexityFixture.Reply(body, true))); var factory = new PerplexityHttpFactory(handler); List<LlmJsonEvent> seen = [];
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(async () =>
        {
            if (sonar) { var provider = new PerplexitySonarProvider(factory); await foreach (var item in provider.StreamAsync(SonarFixture.Payload(), SonarFixture.Context(provider), TestContext.Current.CancellationToken)) seen.Add(item); }
            else { var provider = new PerplexityResponsesProvider(factory); await foreach (var item in provider.StreamAsync(PerplexityFixture.Payload(), PerplexityFixture.Context(provider), TestContext.Current.CancellationToken)) seen.Add(item); }
        });
        Assert.Equal(PerplexityFixture.Bytes(data), failure.Body.ToArray()); Assert.Equal(HttpStatusCode.OK, failure.StatusCode); Assert.Equal("text/event-stream", failure.ContentType); Assert.DoesNotContain(seen, item => item.Type is "response.completed" or "response.failed" or "end_of_stream" || item.Data.TryGetProperty("choices", out var choices) && choices.EnumerateArray().Any(choice => choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind != System.Text.Json.JsonValueKind.Null)); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
