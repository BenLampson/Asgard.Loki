using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Anthropic.Tests.AnthropicHttpFixture;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>原始编码、JSON、生命周期关联和资源边界回归；损坏数据不得靠重编号修复。</summary>
public sealed class NativeMessagesValidationTests
{
    /// <summary>原生扩展流可保留稀疏索引，但这不是完整 Claude content 数组合法性的认证。</summary>
    [Fact]
    public async Task RawSparseIndices_RemainUnchangedWithoutArrayAllocation()
    {
        const string start = "{\"type\":\"content_block_start\",\"index\":2147483647,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}";
        const string delta = "{\"type\":\"content_block_delta\",\"index\":2147483647,\"delta\":{\"type\":\"text_delta\",\"text\":\"原样\"}}";
        const string stop = "{\"type\":\"content_block_stop\",\"index\":2147483647}";
        var wire = Frame(Start) + Frame(start) + Frame(delta) + Frame(stop) + Frame(Finish) + Frame(Stop);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(wire)), true))); List<ClaudeMessageEvent> seen = [];
        await Drain(new(new AnthropicHttpFactory(handler)), TestContext.Current.CancellationToken, seen);
        Assert.Equal(start, seen[1].Data.GetRawText()); Assert.Equal(delta, seen[2].Data.GetRawText()); Assert.Equal(stop, seen[3].Data.GetRawText()); Assert.Equal(6, seen.Count); Assert.Equal(1, handler.Calls);
    }

    /// <summary>变更独立原生源而非预期值，验证每类协议关联故障都拒绝完成。</summary>
    [Theory]
    [InlineData("orphan-delta")] [InlineData("duplicate-index")] [InlineData("duplicate-tool")]
    [InlineData("wrong-delta")] [InlineData("open-block")] [InlineData("repeat-start")]
    [InlineData("repeat-stop")] [InlineData("post-stop-content")] [InlineData("truncated")]
    [InlineData("missing-reason")] [InlineData("fraction-index")] [InlineData("huge-index")]
    public async Task InvalidLifecycle_HasNoSuccessfulTerminal(string kind)
    {
        var source = Golden().GetProperty("events").EnumerateArray().Select(item => JsonNode.Parse(item.GetProperty("data").GetRawText())!.AsObject()).ToList();
        switch (kind)
        {
            case "orphan-delta": source[2]["index"] = 999; break;
            case "duplicate-index": source.Insert(5, source[1].DeepClone().AsObject()); break;
            case "duplicate-tool": var repeated = source[10].DeepClone().AsObject(); repeated["index"] = 13; source.Insert(14, repeated); break;
            case "wrong-delta": source[8]["delta"]!["type"] = "input_json_delta"; break;
            case "open-block": source.RemoveAt(13); break;
            case "repeat-start": source.Insert(1, source[0].DeepClone().AsObject()); break;
            case "repeat-stop": source.Insert(16, source[15].DeepClone().AsObject()); break;
            case "post-stop-content": source.Add(source[7].DeepClone().AsObject()); break;
            case "truncated": source.RemoveAt(15); break;
            case "missing-reason": source.RemoveAt(14); break;
            case "fraction-index": source[1]["index"] = JsonNode.Parse("1.0000000000000000001"); break;
            case "huge-index": source[1]["index"] = JsonNode.Parse("1e999999999"); break;
        }
        var wire = string.Concat(source.Select(item => Frame(item.ToJsonString())));
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(wire)), true))); List<ClaudeMessageEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => Drain(new(new AnthropicHttpFactory(handler)), TestContext.Current.CancellationToken, seen));
        Assert.DoesNotContain(seen, item => item.Type is "message_delta" or "message_stop"); Assert.Equal(1, handler.Calls);
    }
    /// <summary>正文上下界及声明长度同时覆盖完整 JSON 与 SSE；超限不截断或发布终态。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RawByteAndDeclaredLengthLimits_AreExact(bool stream)
    {
        var bytes = Bytes(stream ? Finished : "{\"id\":\"msg\",\"future\":\"中文\"}");
        foreach (var delta in new[] { -1, 0, 1 })
        {
            using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(bytes), stream, bytes.Length + delta)));
            var provider = new AnthropicLlmProvider(new AnthropicHttpFactory(handler), new AnthropicHttpOptions { MaximumBodyBytes = bytes.Length }); List<ClaudeMessageEvent> seen = [];
            var error = await Record.ExceptionAsync(async () => { if (stream) await Drain(provider, TestContext.Current.CancellationToken, seen); else await provider.CreateAsync(new(), Context(provider), TestContext.Current.CancellationToken); });
            if (delta == 0) Assert.Null(error); else { Assert.IsType<InvalidDataException>(error); Assert.DoesNotContain(seen, item => item.Type is "message_delta" or "message_stop"); }
        }
        using var over = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(bytes), stream)));
        var limited = new AnthropicLlmProvider(new AnthropicHttpFactory(over), new AnthropicHttpOptions { MaximumBodyBytes = bytes.Length - 1 });
        await Assert.ThrowsAsync<InvalidDataException>(async () => { if (stream) await Drain(limited, TestContext.Current.CancellationToken, []); else await limited.CreateAsync(new(), Context(limited), TestContext.Current.CancellationToken); });
    }
    /// <summary>重复属性、非法转义、错误编码和数组不成为成功的原生消息。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InvalidJsonAndUtf8_Fail(bool stream)
    {
        foreach (var json in new[] { "{\"type\":\"error\",\"type\":\"message_stop\"}", "{\"future\":{\"x\":0,\"x\":1}}", "{\"future\":\"\\uD800\"}", "[]", "{\"id\":" })
        {
            using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(stream ? Frame(json) : json)), stream)));
            var provider = new AnthropicLlmProvider(new AnthropicHttpFactory(handler));
            var error = await Record.ExceptionAsync(async () => { if (stream) await Drain(provider, TestContext.Current.CancellationToken, []); else await provider.CreateAsync(new(), Context(provider), TestContext.Current.CancellationToken); });
            Assert.True(error is InvalidDataException or JsonException, error?.ToString()); Assert.Equal(1, handler.Calls);
        }
        byte[] bad = [.. Bytes(stream ? "data: {\"x\":\"" : "{\"x\":\""), 0xFF, .. Bytes(stream ? "\"}\n\n" : "\"}")];
        using var invalid = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(bad), stream))); var p = new AnthropicLlmProvider(new AnthropicHttpFactory(invalid));
        await Assert.ThrowsAsync<DecoderFallbackException>(async () => { if (stream) await Drain(p, TestContext.Current.CancellationToken, []); else await p.CreateAsync(new(), Context(p), TestContext.Current.CancellationToken); });
    }
    /// <summary>块关联与终态尾部资源策略均失败关闭，不修剪源数据。</summary>
    [Theory] [InlineData("blocks")] [InlineData("tail")]
    public async Task StateLimits_RejectWithoutTrimming(string limit)
    {
        var bytes = Bytes(Golden().GetProperty("wire").GetString()!); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(bytes), true)));
        var provider = new AnthropicLlmProvider(new AnthropicHttpFactory(handler), new AnthropicHttpOptions { MaximumTrackedContentBlocks = limit == "blocks" ? 1 : 128, MaximumBufferedTerminalEvents = limit == "tail" ? 1 : 128 }); List<ClaudeMessageEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => Drain(provider, TestContext.Current.CancellationToken, seen)); Assert.DoesNotContain(seen, item => item.Type is "message_delta" or "message_stop");
    }
    /// <summary>异步释放特有故障或取消不能被后续同步释放成功掩盖。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task AsyncDispose_FaultsBeforeSuccess(bool stream, bool cancelled)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var source = new AsyncOnlyDisposeStream(Bytes(stream ? Finished : "{\"id\":\"msg\"}"), cancelled, () => { if (cancelled) cancel.Cancel(); });
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(source, stream))); var provider = new AnthropicLlmProvider(new AnthropicHttpFactory(handler)); List<ClaudeMessageEvent> seen = [];
        var error = await Record.ExceptionAsync(async () => { if (stream) await Drain(provider, cancel.Token, seen); else await provider.CreateAsync(new(), Context(provider), cancel.Token); });
        if (cancelled) Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.IsType<IOException>(error); Assert.True(source.AsyncDisposed); Assert.DoesNotContain(seen, item => item.Type is "message_delta" or "message_stop");
    }
    /// <summary>HTTP 200 的原生根级错误保留原始状态和完整正文。</summary>
    [Fact]
    public async Task Http200Error_PreservesNativeFailure()
    {
        const string json = "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"future\":false}}";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(json)), false))); var provider = new AnthropicLlmProvider(new AnthropicHttpFactory(handler));
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => provider.CreateAsync(new(), Context(provider), TestContext.Current.CancellationToken)); Assert.Equal(HttpStatusCode.OK, failure.StatusCode); Assert.Equal(json, failure.ResponseBody); Assert.Equal(1, handler.Calls);
    }
}
