using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;
using Asgard.Loki.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>公共 SDK、原生 SSE 和离线 HTTP 之间验证多候选协议及晚期失败语义。</summary>
public sealed class CandidateStreamProviderTests
{
    /// <summary>最终 HTTP 与全部目标事件快照独立编写，所有终态和共享用量等待真实 EOF 及释放。</summary>
    [Theory]
    [InlineData("chat-mixed")]
    [InlineData("gemini-mixed")]
    [InlineData("chat-parallel-tools")]
    [InlineData("chat-empty")]
    [InlineData("gemini-empty")]
    public async Task PublicStream_MatchesWireAndFullGolden(string name)
    {
        var sample = CandidateStreamFixtures.Case(name); var raw = CandidateStreamFixtures.Source(sample);
        var stream = new GroundingCitationReadStream(Wire(sample));
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var chat = From(sample) == LlmProtocol.ChatCompletions;
            Assert.Equal("https://wire.invalid/v1/" + (chat ? "chat/completions" : "models/model-a:streamGenerateContent?alt=sse"), request.RequestUri!.AbsoluteUri);
            if (chat) Assert.Equal("Bearer bound-key", request.Headers.Authorization!.ToString());
            else Assert.Equal("bound-key", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            Assert.True(JsonNode.DeepEquals(ExpectedRequest(chat), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))));
            return Reply(stream);
        });
        await using var client = ConvertingProviderTests.Build(handler, To(sample), From(sample), options: CandidateStreamFixtures.Options());
        List<LlmJsonEvent> seen = []; await Collect(client, sample, seen, TestContext.Current.CancellationToken, item =>
        { if (TerminalOrUsage(item.Data)) { Assert.True(stream.Eof); Assert.True(stream.Disposed); } });
        Assert.Equal(sample["target"]!.AsArray().Count, seen.Count);
        for (var i = 0; i < seen.Count; i++)
        {
            GeminiCandidateFixtures.Equal(sample["target"]![i]!, seen[i].Data);
            GeminiCandidateFixtures.Equal(raw[sample["sourceIndices"]![i]!.GetValue<int>()], seen[i].Metadata.Conversion!.UpstreamData);
            Assert.Empty(seen[i].Metadata.Conversion!.Diagnostics);
        }
        Assert.Equal(1, handler.Calls); Assert.True(stream.Disposed);
    }

    /// <summary>协议 finish 和用量都已到达，但尾部读取、异步释放或取消仍必须阻止目标成功。</summary>
    [Theory]
    [InlineData("chat-mixed", "read")]
    [InlineData("chat-mixed", "dispose")]
    [InlineData("chat-mixed", "cancel")]
    [InlineData("gemini-mixed", "read")]
    [InlineData("gemini-mixed", "dispose")]
    [InlineData("gemini-mixed", "cancel")]
    public async Task TransportTailFailure_DoesNotReleaseAnyFinish(string name, string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var sample = CandidateStreamFixtures.Case(name);
        var stream = new GroundingCitationReadStream(Wire(sample))
        { FailAtEof = kind == "read", FailDispose = kind == "dispose", OnEof = kind == "cancel" ? cancellation.Cancel : null };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(stream)));
        await using var client = ConvertingProviderTests.Build(handler, To(sample), From(sample), options: CandidateStreamFixtures.Options());
        List<LlmJsonEvent> seen = [];
        if (kind == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(client, sample, seen, cancellation.Token));
        else await Assert.ThrowsAsync<IOException>(() => Collect(client, sample, seen, cancellation.Token));
        Assert.All(seen, e => Assert.False(TerminalOrUsage(e.Data))); Assert.True(stream.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>请求明确超过接收候选上限时应先失败，不能发送后再丢弃多余候选。</summary>
    [Fact]
    public async Task RequestCountOverPolicy_FailsBeforeHttp()
    {
        var sample = CandidateStreamFixtures.Case("chat-mixed");
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await using var client = ConvertingProviderTests.Build(handler, To(sample), From(sample),
            options: CandidateStreamFixtures.Options() with { MaxStreamCandidates = 1 });
        var error = await Assert.ThrowsAsync<LlmConversionException>(() => Collect(client, sample, [], TestContext.Current.CancellationToken));
        Assert.Contains(error.Diagnostics, d => d.Code == "stream_candidate_limit"); Assert.Equal(0, handler.Calls);
    }

    /// <summary>开启模式或改变显式资源配置会使已有 Session 校验失败，同配置仍可跨客户端恢复。</summary>
    [Theory]
    [InlineData("mode")]
    [InlineData("policy")]
    [InlineData("unchanged")]
    public async Task Session_BindsExplicitModeAndPolicies(string kind)
    {
        var sample = CandidateStreamFixtures.Case("chat-mixed");
        var store = new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock());
        var reply = GeminiCandidateFixtures.Read("chat-gemini-complete", "single-nonzero")["source"]!;
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            return body["stream"]!.GetValue<bool>() ? Reply(new GroundingCitationReadStream(Wire(sample)))
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
        });
        var original = CandidateStreamFixtures.Options() with { EnableMultipleCandidateStreams = kind != "mode" };
        await using var first = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.ChatCompletions, store, original);
        var request = Request(sample); request["generationConfig"]!["candidateCount"] = 1;
        await ConvertingProviderTests.CompleteAsync(first, LlmProtocol.Gemini, request);
        var changed = kind == "mode" ? original with { EnableMultipleCandidateStreams = true }
            : kind == "policy" ? original with { MaxStreamCandidates = 3 } : original with { };
        await using var second = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.ChatCompletions, store, changed);
        if (kind == "unchanged")
        {
            List<LlmJsonEvent> seen = []; await Collect(second, sample, seen, TestContext.Current.CancellationToken);
            Assert.Equal(sample["target"]!.AsArray().Count, seen.Count); Assert.Equal(2, handler.Calls);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(second, sample, [], TestContext.Current.CancellationToken));
            Assert.Equal(1, handler.Calls);
        }
    }

    /// <summary>原始 SSE 由测试样本直接构造，Chat 的 DONE 属于真实替代上游，转换器不生成它。</summary>
    private static string Wire(JsonObject sample) => string.Concat(CandidateStreamFixtures.Source(sample).Select(e => "data: " + e.ToJsonString() + "\n\n"))
        + (From(sample) == LlmProtocol.ChatCompletions ? "data: [DONE]\n\n" : "");
    /// <summary>只用本地替代响应流，不发真实供应商请求。</summary>
    private static HttpResponseMessage Reply(Stream stream) => new(HttpStatusCode.OK)
    { Content = new StreamContent(stream) { Headers = { ContentType = new("text/event-stream") } } };
    /// <summary>测试的原生源协议。</summary>
    private static LlmProtocol From(JsonObject sample) => Enum.Parse<LlmProtocol>(sample["from"]!.GetValue<string>());
    /// <summary>测试的公共调用协议。</summary>
    private static LlmProtocol To(JsonObject sample) => Enum.Parse<LlmProtocol>(sample["to"]!.GetValue<string>());
    /// <summary>数量请求只在已实现的 Gemini 到 Chat 方向设置，不猜测反向支持。</summary>
    private static JsonObject Request(JsonObject sample) => JsonNode.Parse(To(sample) == LlmProtocol.Gemini
        ? """{"model":"public","contents":[{"role":"user","parts":[{"text":"hello"}]}],"generationConfig":{"maxOutputTokens":32,"candidateCount":2},"tools":[{"functionDeclarations":[{"name":"lookup","parametersJsonSchema":{"type":"object"}}]}]}"""
        : """{"model":"public","messages":[{"role":"user","content":"hello"}],"max_completion_tokens":32,"tools":[{"type":"function","function":{"name":"lookup","parameters":{"type":"object"}}}]}""")!.AsObject();
    /// <summary>完整预期 HTTP 独立于转换器结果，包含模型、原生工具与流式控制。</summary>
    private static JsonObject ExpectedRequest(bool chat) => JsonNode.Parse(chat
        ? """{"model":"model-a","messages":[{"role":"user","content":[{"type":"text","text":"hello"}]}],"max_completion_tokens":32,"tools":[{"type":"function","function":{"name":"lookup","parameters":{"type":"object"}}}],"n":2,"stream":true}"""
        : """{"contents":[{"role":"user","parts":[{"text":"hello"}]}],"generationConfig":{"maxOutputTokens":32},"tools":[{"functionDeclarations":[{"name":"lookup","parametersJsonSchema":{"type":"object"}}]}]}""")!.AsObject();
    /// <summary>只在实际公开枚举完成后接受结束项，不借用缓存来恢复网络连接。</summary>
    private static async Task Collect(ILokiClient client, JsonObject sample, List<LlmJsonEvent> seen, CancellationToken token, Action<LlmJsonEvent>? observe = null)
    {
        var request = Request(sample); var options = new LlmCallOptions { SessionId = "wire-session" };
        if (To(sample) == LlmProtocol.Gemini)
        {
            await foreach (var item in client.LLM.Gemini.StreamAsync(request.Deserialize<GeminiContentRequest>()!, options, token)) { observe?.Invoke(item); seen.Add(item); }
        }
        else await foreach (var item in client.LLM.ChatCompletions.StreamAsync(request.Deserialize<ChatCompletionRequest>()!, options, token)) { observe?.Invoke(item); seen.Add(item); }
    }
    /// <summary>只检查原生结束字段或独立共享用量，不扫描函数业务参数。</summary>
    private static bool TerminalOrUsage(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices)) return choices.GetArrayLength() == 0 && root.TryGetProperty("usage", out _)
            || choices.EnumerateArray().Any(c => c.GetProperty("finish_reason").ValueKind != JsonValueKind.Null);
        var candidates = root.GetProperty("candidates"); return candidates.GetArrayLength() == 0 && root.TryGetProperty("usageMetadata", out _)
            || candidates.EnumerateArray().Any(c => c.TryGetProperty("finishReason", out _));
    }
}
