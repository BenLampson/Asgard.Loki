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

/// <summary>公共 Gemini SDK 的多文本拼接、完整 HTTP 与尾部故障和 Session 配置验收。</summary>
public sealed class ConcatenatedCitationProviderTests
{
    /// <summary>三种源单位与提前空 part 开始均逐事件匹配独立金样，正文保持即时，引用等待完整释放。</summary>
    [Theory] [InlineData(LlmCitationIndexUnit.UnicodeScalar, false)] [InlineData(LlmCitationIndexUnit.Utf16CodeUnit, false)] [InlineData(LlmCitationIndexUnit.Utf8Byte, false)] [InlineData(LlmCitationIndexUnit.UnicodeScalar, true)] [InlineData(LlmCitationIndexUnit.Utf16CodeUnit, true)] [InlineData(LlmCitationIndexUnit.Utf8Byte, true)]
    public async Task FullPublicSdk_MatchesAuthoredWireAndEvents(LlmCitationIndexUnit unit, bool earlyPart)
    {
        var source = ConcatenatedCitationFixtures.Source(unit); if (earlyPart) ConcatenatedCitationFixtures.StartNextPartEarly(source); var stream = new GroundingCitationReadStream(Wire(source)); using var handler = Handler(stream, true); await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.Responses, options: ConcatenatedCitationFixtures.Options(unit)); List<LlmJsonEvent> seen = [];
        await foreach (var item in Stream(client, TestContext.Current.CancellationToken)) { var candidate = item.Data.GetProperty("candidates")[0]; if (candidate.TryGetProperty("finishReason", out _)) { Assert.True(stream.Eof); Assert.True(stream.Disposed); } seen.Add(item); }
        var expected = ConcatenatedCitationFixtures.Read("Gemini").AsArray(); if (earlyPart) { var text = expected[1]!.DeepClone(); expected[1] = expected[2]!.DeepClone(); expected[2] = text; } Assert.Equal(expected.Count, seen.Count); for (var i = 0; i < seen.Count; i++) GroundingCitationStreamFixtures.Equal(expected[i]!, seen[i].Data); Assert.All(seen, item => { Assert.Empty(item.Metadata.Conversion!.Diagnostics); Assert.Equal(LlmProtocol.Responses, item.Metadata.Conversion.UpstreamProtocol); Assert.Equal(LlmProtocol.Gemini, item.Metadata.Conversion.PublicProtocol); }); GroundingCitationStreamFixtures.Equal(source[^1], seen[^1].Metadata.Conversion!.UpstreamData); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生 completed 后的迟读、释放或取消不能交付拼接后的引用/终态。</summary>
    [Theory] [InlineData("read")] [InlineData("dispose")] [InlineData("cancel")]
    public async Task LateTransportFailure_HasNoGroundingOrCompletion(string kind)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new GroundingCitationReadStream(Wire(ConcatenatedCitationFixtures.Source())) { FailAtEof = kind == "read", FailDispose = kind == "dispose", OnEof = kind == "cancel" ? cancel.Cancel : null }; using var handler = Handler(body); await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.Responses, options: ConcatenatedCitationFixtures.Options()); List<LlmJsonEvent> seen = [];
        if (kind == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(client, seen, cancel.Token)); else await Assert.ThrowsAsync<IOException>(() => Collect(client, seen, cancel.Token)); NoTerminal(seen); Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>实际原生流的非连续正文、错快照、错范围或截断不能通过拼接恢复成成功。</summary>
    [Theory] [InlineData("interleaved")] [InlineData("snapshot")] [InlineData("range")] [InlineData("truncated")] [InlineData("part-identity")] [InlineData("reordered")]
    public async Task InvalidNativeSource_WithholdsSuccessfulTarget(string kind)
    {
        var source = ConcatenatedCitationFixtures.Source(); if (kind == "interleaved") ConcatenatedCitationFixtures.Interleave(source); if (kind == "reordered") ConcatenatedCitationFixtures.ReorderIdenticalParts(source); if (kind == "part-identity") foreach (var item in source.Where(item => item["output_index"]?.GetValue<int>() == 0 && item.ContainsKey("content_index"))) item["content_index"] = item["content_index"]!.GetValue<int>() + 4; if (kind == "snapshot") source[^1]["response"]!["output"]![0]!["content"]![1]!["annotations"]![0]!["title"] = "changed"; if (kind == "range") ConcatenatedCitationFixtures.ChangeCitation(source, false, citation => citation["end_index"] = 999); if (kind == "truncated") source.RemoveAt(source.Count - 1); using var handler = Handler(new GroundingCitationReadStream(Wire(source))); await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.Responses, options: ConcatenatedCitationFixtures.Options() with { AllowLoss = true }); List<LlmJsonEvent> seen = []; var error = await Record.ExceptionAsync(() => Collect(client, seen, TestContext.Current.CancellationToken)); Assert.NotNull(error); if (kind is "interleaved" or "reordered") Assert.Contains(Assert.IsType<LlmConversionException>(error).Diagnostics, item => item.Code == "grounding_stream_layout_not_representable"); else Assert.IsType<InvalidDataException>(error); NoTerminal(seen); Assert.Equal(1, handler.Calls);
    }
    /// <summary>拼接部分文本不改变上游明确的长度/安全终态。</summary>
    [Theory] [InlineData("max_output_tokens", "MAX_TOKENS")] [InlineData("content_filter", "SAFETY")]
    public async Task IncompleteStatus_IsNeverUpgraded(string reason, string finish)
    {
        var source = ConcatenatedCitationFixtures.Source(); source[^1]["type"] = "response.incomplete"; source[^1]["response"]!["status"] = "incomplete"; source[^1]["response"]!["incomplete_details"] = new JsonObject { ["reason"] = reason }; using var handler = Handler(new GroundingCitationReadStream(Wire(source))); await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.Responses, options: ConcatenatedCitationFixtures.Options()); List<LlmJsonEvent> seen = []; await Collect(client, seen, TestContext.Current.CancellationToken); Assert.Equal(finish, seen[^1].Data.GetProperty("candidates")[0].GetProperty("finishReason").GetString()); Assert.Equal(4, seen[^1].Data.GetProperty("candidates")[0].GetProperty("groundingMetadata").GetProperty("groundingSupports").GetArrayLength());
    }
    /// <summary>新模式写入既有选项字段，只在明确切换模式时使已有 Session 不匹配。</summary>
    [Fact]
    public async Task ChangedMode_InvalidatesExistingSessionBeforeSecondHttp()
    {
        var store = Store(); using var handler = Handler(new GroundingCitationReadStream(Wire(ResponseCitationStreamFixtures.Source()))); await using (var first = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.Responses, store, ResponseCitationStreamFixtures.Options())) await Collect(first, [], TestContext.Current.CancellationToken); await using var next = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.Responses, store, ConcatenatedCitationFixtures.Options()); await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(next, [], TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>相同拼接模式跨客户端恢复完整目标，不改模式或重选实际模型。</summary>
    [Fact]
    public async Task SameMode_RecoversSessionAcrossClients()
    {
        var store = Store(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new GroundingCitationReadStream(Wire(ConcatenatedCitationFixtures.Source()))))); await using (var first = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.Responses, store, ConcatenatedCitationFixtures.Options())) await Collect(first, [], TestContext.Current.CancellationToken); var before = await store.GetAsync("concat-session", TestContext.Current.CancellationToken); await using (var next = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.Responses, store, ConcatenatedCitationFixtures.Options())) await Collect(next, [], TestContext.Current.CancellationToken); var after = await store.GetAsync("concat-session", TestContext.Current.CancellationToken); Assert.Equal(before!.Fingerprint, after!.Fingerprint); Assert.Equal(2, handler.Calls);
    }
    /// <summary>不能把目标拼接声明误用于 Gemini 源流，实际 HTTP 前明确失败。</summary>
    [Fact]
    public async Task WrongDirection_IsRejectedBeforeHttp()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, LlmProtocol.Gemini, options: ConcatenatedCitationFixtures.Options()); await Assert.ThrowsAsync<ArgumentException>(() => ConvertingProviderTests.StreamAsync(client, LlmProtocol.Responses, JsonNode.Parse("""{"model":"public","input":"sources"}""")!.AsObject())); Assert.Equal(0, handler.Calls);
    }
    /// <summary>增量后提前退出/取消释放来源，不强制读到最后一条消息或合成完成。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task EarlyExit_ReleasesWithoutCompleting(bool cancelAfterFirst)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new GroundingCitationReadStream(Wire(ConcatenatedCitationFixtures.Source())); using var handler = Handler(body); await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.Responses, options: ConcatenatedCitationFixtures.Options()); var count = 0; async Task Consume() { await foreach (var _ in Stream(client, cancel.Token)) { count++; if (cancelAfterFirst) cancel.Cancel(); else break; } } if (cancelAfterFirst) await Assert.ThrowsAnyAsync<OperationCanceledException>(Consume); else await Consume(); Assert.Equal(1, count); Assert.False(body.Eof); Assert.True(body.Disposed);
    }
    /// <summary>独立完整请求快照检查最终地址、认证、实际模型和方法流标志。</summary>
    private static StubHttpHandler Handler(Stream body, bool verify = false) => new(async (request, token) => { if (verify) { Assert.Equal("https://wire.invalid/v1/responses", request.RequestUri!.AbsoluteUri); Assert.Equal("bound-key", request.Headers.Authorization!.Parameter); Assert.Equal("Bearer", request.Headers.Authorization.Scheme); Assert.True(JsonNode.DeepEquals(ConcatenatedCitationFixtures.Read("Wire"), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); } return Reply(body); });
    /// <summary>普通 HTTP 替代响应，不触发真实模型。</summary>
    private static HttpResponseMessage Reply(Stream body) { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new("text/event-stream"); return response; }
    /// <summary>测试只序列化手工原生事件，不从生产转换器生成源或预期。</summary>
    private static string Wire(IEnumerable<JsonObject> source) => string.Concat(source.Select(item => "data: " + item.ToJsonString() + "\n\n"));
    /// <summary>普通 MemoryDistributedCache 与显式协调器，不依赖 Redis。</summary>
    private static ILlmSessionStore Store() => new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock());
    /// <summary>固定公开请求和 Session，可跨客户端复用相同完整目标。</summary>
    private static IAsyncEnumerable<GeminiContentEvent> Stream(ILokiClient client, CancellationToken token) => client.LLM.Gemini.StreamAsync(ConcatenatedCitationFixtures.Read("Request").Deserialize<GeminiContentRequest>()!, new() { SessionId = "concat-session" }, token);
    /// <summary>保留失败前已输出事件，显式检查没有提前成功。</summary>
    private static async Task Collect(ILokiClient client, List<LlmJsonEvent> seen, CancellationToken token) { await foreach (var item in Stream(client, token)) seen.Add(item); }
    /// <summary>所有失败前事件都不能携带引用或终态。</summary>
    private static void NoTerminal(IEnumerable<LlmJsonEvent> seen) => Assert.All(seen, item => { var candidate = item.Data.GetProperty("candidates")[0]; Assert.False(candidate.TryGetProperty("groundingMetadata", out _)); Assert.False(candidate.TryGetProperty("finishReason", out _)); });
}
