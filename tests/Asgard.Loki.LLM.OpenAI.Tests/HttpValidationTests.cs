using System.Text;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>原始字节和原生终态结构检查，不把工具参数或未来对象解释为业务状态。</summary>
public sealed class HttpValidationTests
{
    /// <summary>正文上限和实际长度检查同时覆盖 JSON/SSE 以及两种原生协议。</summary>
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task BodyBoundaries_AreExact(bool responses, bool streaming)
    {
        var wire = streaming ? Finished(responses) : "{\"id\":\"native\",\"future\":\"中文\"}"; var bytes = Bytes(wire);
        foreach (var delta in new[] { -1, 0, 1 })
        {
            var body = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, streaming, bytes.Length + delta)));
            var provider = new OpenAiLlmProvider(new OpenAiHttpFactory(handler), new OpenAiHttpOptions { MaximumBodyBytes = bytes.Length }); List<LlmJsonEvent> items = [];
            var error = await Record.ExceptionAsync(async () => { if (streaming) await Drain(provider, responses, TestContext.Current.CancellationToken, items); else await Complete(provider, responses, TestContext.Current.CancellationToken); });
            if (delta == 0) Assert.Null(error); else { Assert.IsType<InvalidDataException>(error); Assert.Empty(items); }
            Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
        }
        using var over = new StubHttpHandler((_, _) => Task.FromResult(Reply(new(bytes), streaming)));
        var limited = new OpenAiLlmProvider(new OpenAiHttpFactory(over), new OpenAiHttpOptions { MaximumBodyBytes = bytes.Length - 1 });
        await Assert.ThrowsAsync<InvalidDataException>(async () => { if (streaming) await Drain(limited, responses, TestContext.Current.CancellationToken, []); else await Complete(limited, responses, TestContext.Current.CancellationToken); });
    }

    /// <summary>不接受重复属性、替换编码、非法字符串转义或非对象结果。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task InvalidNativeJson_IsRejected(bool streaming)
    {
        foreach (var text in new[] { "{\"id\":\"a\",\"id\":\"b\"}", "{\"future\":{\"x\":1,\"x\":2}}", "{\"future\":\"\\uD800\"}", "[]", "{\"id\":" })
        {
            using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new(Bytes(streaming ? Frame(text) + Finished(true) : text)), streaming)));
            var error = await Record.ExceptionAsync(async () => { var p = new OpenAiLlmProvider(new OpenAiHttpFactory(handler)); if (streaming) await Drain(p, true, TestContext.Current.CancellationToken, []); else await Complete(p, true, TestContext.Current.CancellationToken); });
            Assert.True(error is JsonException or InvalidDataException, error?.ToString()); Assert.Equal(1, handler.Calls);
        }
        byte[] invalid = [.. Bytes(streaming ? "data: {\"future\":\"" : "{\"future\":\""), 0xFF, .. Bytes(streaming ? "\"}\n\n" : "\"}")];
        using var bad = new StubHttpHandler((_, _) => Task.FromResult(Reply(new(invalid), streaming)));
        await Assert.ThrowsAsync<DecoderFallbackException>(async () => { var p = new OpenAiLlmProvider(new OpenAiHttpFactory(bad)); if (streaming) await Drain(p, false, TestContext.Current.CancellationToken, []); else await Complete(p, false, TestContext.Current.CancellationToken); });
    }

    /// <summary>Responses 身份、状态、终态次数及终态后重开均严格检查。</summary>
    [Theory]
    [InlineData("data: [DONE]\n\n", false)]
    [InlineData("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"r\"}}\n\n", false)]
    [InlineData("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"failed\"}}\n\n", false)]
    [InlineData("data: {\"type\":\"response.created\",\"response\":{\"id\":\"other\"}}\n\n", true)]
    [InlineData("data: {\"type\":\"response.output_text.delta\",\"delta\":\"late\"}\n\n", true)]
    public async Task InvalidResponsesLifecycle_IsRejected(string frame, bool afterTerminal)
    {
        // 缺失/不匹配状态必须在首次终态处失败，不能被“重复终态”检查掩盖。
        var wire = afterTerminal ? Frame(ResponseFinish) + frame : frame + Frame(ResponseFinish);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new(Bytes(wire)), true))); List<LlmJsonEvent> items = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => Drain(new(new OpenAiHttpFactory(handler)), true, TestContext.Current.CancellationToken, items)); Assert.Empty(items);
    }

    /// <summary>有限尾部资源策略参与绑定；缺失 Chat DONE 不从 EOF 合成。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TerminalBuffer_IsBounded(bool responses)
    {
        var wire = Frame(responses ? ResponseFinish : ChatFinish) + Frame("{\"type\":\"future\"}") + (responses ? "" : Frame("[DONE]"));
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new(Bytes(wire)), true)));
        var p = new OpenAiLlmProvider(new OpenAiHttpFactory(handler), new OpenAiHttpOptions { MaximumBufferedTerminalEvents = 1 }); List<LlmJsonEvent> items = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => Drain(p, responses, TestContext.Current.CancellationToken, items)); Assert.Empty(items);
    }

    /// <summary>Chat 原生 finish_reason 不能替代必需 DONE。</summary>
    [Fact]
    public async Task ChatFinishWithoutDone_IsTruncation()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new(Bytes(Frame(ChatFinish))), true))); List<LlmJsonEvent> items = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => Drain(new(new OpenAiHttpFactory(handler)), false, TestContext.Current.CancellationToken, items)); Assert.Empty(items);
    }

    /// <summary>新 HTTP 域、命名客户端和显式资源策略均不能沿用旧会话身份。</summary>
    [Fact]
    public void BindingIdentity_TracksHttpContractAndPolicy()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new OpenAiHttpFactory(handler);
        var standard = new OpenAiLlmProvider(factory); Assert.Contains("openai-http-v2", standard.BindingIdentity); Assert.NotEqual(typeof(OpenAiLlmProvider).AssemblyQualifiedName, standard.BindingIdentity);
        Assert.Equal(standard.BindingIdentity, new OpenAiLlmProvider(factory, new OpenAiHttpOptions()).BindingIdentity);
        Assert.NotEqual(standard.BindingIdentity, new OpenAiLlmProvider(factory, httpClientName: "other").BindingIdentity);
        Assert.NotEqual(standard.BindingIdentity, new OpenAiLlmProvider(factory, new OpenAiHttpOptions { MaximumBodyBytes = 10 }).BindingIdentity);
        Assert.NotEqual(standard.BindingIdentity, new OpenAiLlmProvider(factory, new OpenAiHttpOptions { MaximumBufferedTerminalEvents = 1 }).BindingIdentity);
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenAiLlmProvider(factory, new OpenAiHttpOptions { MaximumBodyBytes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenAiLlmProvider(factory, new OpenAiHttpOptions { MaximumBufferedTerminalEvents = 0 })); Assert.Empty(factory.Clients);
    }
}
