using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>通过实际公共 Gemini 调用和固定 Chat 上游验证计数及完整候选返回。</summary>
public sealed class CandidateCallProviderTests
{
    /// <summary>最终 HTTP 同时包含原计数、推理、Schema 约束，返回所有候选及原始元数据。</summary>
    [Theory]
    [InlineData("count-settings")]
    [InlineData("count-one")]
    public async Task CandidateCount_CompleteCallMatchesFullGoldens(string name)
    {
        var sample = GeminiCandidateFixtures.Read("gemini-chat-requests", name); var raw = sample["reply"]!.AsObject();
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("https://wire.invalid/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer bound-key", request.Headers.Authorization!.ToString());
            var expected = sample["target"]!.DeepClone().AsObject(); expected["stream"] = false;
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))));
            return Reply(raw);
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.ChatCompletions,
            options: GeminiCandidateCountTests.Options());
        var source = sample["source"]!.DeepClone().AsObject(); source["model"] = "public"; var before = source.ToJsonString();
        var result = await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Gemini, source);
        GeminiCandidateFixtures.Equal(sample["result"]!, result.Data);
        GeminiCandidateFixtures.Equal(raw, result.Metadata.Conversion!.UpstreamData);
        Assert.Empty(result.Metadata.Conversion.Diagnostics); Assert.Equal(1, handler.Calls); Assert.Equal(before, source.ToJsonString());
        Assert.Equal("public", result.Metadata.PublicModel); Assert.Equal("model-a", result.Metadata.UpstreamModel);
    }

    /// <summary>独立反向响应样本覆盖公共装饰器中的工具、引用、稀疏索引和空文本。</summary>
    [Theory]
    [InlineData("text-two")]
    [InlineData("tools-isolated")]
    [InlineData("citations-isolated")]
    [InlineData("single-nonzero")]
    [InlineData("empty-and-filtered")]
    public async Task ReverseCompleteCall_MatchesIndependentReply(string name)
    {
        var sample = GeminiCandidateFixtures.Read("chat-gemini-complete", name); var raw = sample["source"]!.AsObject();
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var expected = JsonNode.Parse("""{"model":"model-a","messages":[{"role":"user","content":[{"type":"text","text":"hello"}]}],"max_completion_tokens":32,"stream":false}""")!;
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))));
            return Reply(raw);
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.ChatCompletions,
            options: GeminiCandidateCountTests.Options());
        var result = await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Gemini, BasicRequest());
        GeminiCandidateFixtures.Equal(sample["target"]!, result.Data); GeminiCandidateFixtures.Equal(raw, result.Metadata.Conversion!.UpstreamData);
        Assert.Empty(result.Metadata.Conversion.Diagnostics); Assert.Equal(1, handler.Calls);
    }

    /// <summary>非法候选数量在完整 SDK 路径中失败，不能先发出请求再删字段补救。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("\"2\"")]
    [InlineData("true")]
    public async Task InvalidCount_IsRejectedBeforeHttp(string count)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.ChatCompletions,
            options: GeminiCandidateCountTests.Options(true));
        var source = GeminiCandidateCountTests.Request(); source["model"] = "public"; source["generationConfig"]!["candidateCount"] = JsonNode.Parse(count);
        await Assert.ThrowsAsync<InvalidDataException>(() => ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Gemini, source));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>多候选流式入口即使用户提供 false，也由调用模式决定在 HTTP 前拒绝。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultiCandidateStream_IsRejectedBeforeHttp(bool allowLoss)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.ChatCompletions,
            options: GeminiCandidateCountTests.Options(allowLoss));
        var source = GeminiCandidateCountTests.Request(); source["model"] = "public"; source["stream"] = false;
        var error = await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.StreamAsync(client, LlmProtocol.Gemini, source));
        Assert.Contains(error.Diagnostics, d => d.Code == "multiple_candidate_stream_not_supported"); Assert.Equal(0, handler.Calls);
    }

    /// <summary>直接使用适配器也不能通过省略或伪造 stream 字段绕开实际流式模式。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectAdapterStream_DoesNotAcquireUpstream(bool includeFalse)
    {
        var native = new CredentialChatProvider(); var provider = new ConvertingLlmProvider(native, LlmProtocol.ChatCompletions,
            "fixed-chat", GeminiCandidateCountTests.Options());
        var target = new LlmTarget { Id = "target", ProviderId = provider.Id, BaseAddress = new("https://wire.invalid/v1/"),
            UpstreamModel = "model-a", CredentialId = "key", Protocols = LlmProtocol.Gemini };
        var context = new LlmAdapterContext(target, new("key", "bound-key"), "public", "direct");
        var source = GeminiCandidateCountTests.Request(); if (includeFalse) source["stream"] = false;
        await Assert.ThrowsAsync<LlmConversionException>(async () =>
        {
            await foreach (var item in ((IGeminiAdapter)provider).StreamAsync(source, context, TestContext.Current.CancellationToken))
                Assert.Fail("不支持的多候选流式不能交付成功事件。");
        });
        Assert.Empty(native.Calls);
    }

    /// <summary>后续候选异常导致整次转换失败，没有重试、候选零降级或目标切换。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidLaterChoice_FailsWholeCallWithoutRetry(bool unknownFinish)
    {
        var raw = GeminiCandidateFixtures.Read("chat-gemini-complete", "text-two")["source"]!.AsObject();
        if (unknownFinish) raw["choices"]![1]!["finish_reason"] = "future"; else raw["choices"]![1]!["index"] = 7;
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(raw)));
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.ChatCompletions,
            options: GeminiCandidateCountTests.Options(true));
        if (unknownFinish) await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Gemini, BasicRequest()));
        else await Assert.ThrowsAsync<InvalidDataException>(() => ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Gemini, BasicRequest()));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>同一固定目标 Session 的候选数量可以按次变化，不调度其他模型或凭据。</summary>
    [Fact]
    public async Task CandidateCount_CanChangeWithinBoundSession()
    {
        var first = GeminiCandidateFixtures.Read("gemini-chat-requests", "count-settings");
        var second = GeminiCandidateFixtures.Read("gemini-chat-requests", "count-one"); var calls = 0;
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var sample = calls++ == 0 ? first : second;
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            Assert.Equal(sample["target"]!["n"]!.GetValue<int>(), body["n"]!.GetValue<int>());
            Assert.Equal("model-a", body["model"]!.GetValue<string>()); return Reply(sample["reply"]!.AsObject());
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.ChatCompletions,
            options: GeminiCandidateCountTests.Options());
        foreach (var sample in new[] { first, second })
        {
            var request = sample["source"]!.DeepClone().AsObject(); request["model"] = "public";
            var result = await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Gemini, request);
            GeminiCandidateFixtures.Equal(sample["result"]!, result.Data);
            Assert.Equal("target", result.Metadata.TargetId);
        }
        Assert.Equal(2, handler.Calls);
    }

    /// <summary>单候选流式仍可经过原生适配器正常完成，新的前置检查不是全面禁止此入口。</summary>
    [Fact]
    public async Task DirectOneCandidateStream_RetainsExistingLifecycle()
    {
        var native = new CredentialChatProvider(); var provider = new ConvertingLlmProvider(native, LlmProtocol.ChatCompletions,
            "fixed-chat", GeminiCandidateCountTests.Options());
        var target = new LlmTarget { Id = "target", ProviderId = provider.Id, BaseAddress = new("https://wire.invalid/v1/"),
            UpstreamModel = "model-a", CredentialId = "key", Protocols = LlmProtocol.Gemini };
        var context = new LlmAdapterContext(target, new("key", "bound-key"), "public", "single-stream");
        var source = GeminiCandidateFixtures.Read("gemini-chat-requests", "count-one")["source"]!.AsObject();
        List<GeminiContentEvent> events = [];
        await foreach (var item in ((IGeminiAdapter)provider).StreamAsync(source, context, TestContext.Current.CancellationToken)) events.Add(item);
        Assert.Equal(1, Assert.Single(native.Calls).Request["n"]!.GetValue<int>());
        Assert.Contains(events, item => item.Data.TryGetProperty("candidates", out var candidates)
            && candidates.EnumerateArray().Any(candidate => candidate.TryGetProperty("finishReason", out var finish) && finish.GetString() == "STOP"));
        Assert.Equal(3, events[^1].Data.GetProperty("usageMetadata").GetProperty("totalTokenCount").GetInt32());
    }

    /// <summary>公共模型由现有核心按固定目标改写，不在转换器推断模型或服务器。</summary>
    private static JsonObject BasicRequest() => JsonNode.Parse("""{"model":"public","contents":[{"role":"user","parts":[{"text":"hello"}]}],"generationConfig":{"maxOutputTokens":32}}""")!.AsObject();
    /// <summary>原生 Chat 完整响应由本地替代处理器提供，不调用真实模型。</summary>
    private static HttpResponseMessage Reply(JsonObject raw) => new(HttpStatusCode.OK)
    { Content = new StringContent(raw.ToJsonString(), Encoding.UTF8, "application/json") };
}
