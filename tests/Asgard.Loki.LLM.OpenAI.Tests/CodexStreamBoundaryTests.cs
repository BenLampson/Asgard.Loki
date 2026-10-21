using System.Net.Http.Headers;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.CodexFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>复用原生 Responses 终态规则，并在 Codex 来源/账户/资源边界之后才交付终态。</summary>
public sealed class CodexStreamBoundaryTests
{
    /// <summary>来源、长度/字节/尾部、EOF/关闭/取消/目标等晚到错误不能先发布终态。</summary>
    [Theory] [InlineData("source")] [InlineData("expired")] [InlineData("read")] [InlineData("body-dispose")] [InlineData("client-dispose")] [InlineData("cancel-eof")] [InlineData("cancel-body")] [InlineData("cancel-client")] [InlineData("late-target")] [InlineData("length")] [InlineData("bytes")] [InlineData("tail")] [InlineData("truncated")] [InlineData("utf8")] [InlineData("charset")]
    public async Task LateFailure_DoesNotDeliverTerminal(string fault)
    {
        using var cancel = new CancellationTokenSource(); using var source = new CodexFixtureSource(); var clock = new CodexTestClock(); var text = Text("events.sse"); if (fault == "truncated") text = text.TrimEnd(); var bytes = Bytes(text); if (fault == "utf8") bytes = [..bytes, 0xff];
        var body = new OllamaBodyStream(bytes) { FailAtEof = fault == "read", FailDispose = fault == "body-dispose", OnEof = () => { if (fault == "source") source.BindingIdentity = "changed"; if (fault == "expired") clock.Current = Now.AddHours(1); if (fault == "cancel-eof") cancel.Cancel(); }, OnDispose = fault == "cancel-body" ? cancel.Cancel : null };
        HttpRequestMessage? actual = null; using var handler = new StubHttpHandler((message, _) => { actual = message; var response = OpenAiHttpFixture.Reply(body, true, fault == "length" ? bytes.Length + 1 : null); if (fault == "charset") response.Content.Headers.ContentType!.CharSet = "utf-16"; return Task.FromResult(response); });
        var factory = new OpenAiHttpFactory(handler) { OnDispose = () => { if (fault == "client-dispose") throw new IOException("client close"); if (fault == "cancel-client") cancel.Cancel(); if (fault == "late-target") actual!.RequestUri = new("https://other.invalid/responses"); } };
        var options = Options(); if (fault == "bytes") options = options with { MaximumResponseBytes = bytes.Length - 1 }; if (fault == "tail") options = options with { MaximumBufferedTerminalEvents = 1 };
        List<ResponseEvent> events = []; await Assert.ThrowsAnyAsync<Exception>(() => Stream(Provider(factory, source, options, clock), cancel.Token, events.Add)); Assert.DoesNotContain(events, x => x.Type is "response.completed" or "response.incomplete" or "response.failed"); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls);
    }
    /// <summary>缺失/重复/身份变化/晚到生成不修补；原生 late error 保持原始正文。</summary>
    [Theory] [InlineData("missing")] [InlineData("duplicate")] [InlineData("identity")] [InlineData("late-generation")] [InlineData("late-error")]
    public async Task NativeTerminalAndIdentity_AreNotSynthesized(string fault)
    {
        using var expected = JsonDocument.Parse(Text("events.json")); var frames = expected.RootElement.EnumerateArray().Select(x => x.GetRawText()).ToList();
        const string error = "{\"type\":\"error\",\"error\":{\"message\":\"late native failure\",\"exact\":9007199254740993}}";
        if (fault == "missing") frames.RemoveAt(9); if (fault == "duplicate") frames.Insert(10, frames[9]); if (fault == "identity") frames[9] = frames[9].Replace("resp-codex-a", "other", StringComparison.Ordinal); if (fault == "late-generation") frames.Add(frames[4]); if (fault == "late-error") frames.Add(error);
        var wire = string.Concat(frames.Select(x => "data: " + x + Environment.NewLine + Environment.NewLine)); var body = new OllamaBodyStream(Bytes(wire)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(OpenAiHttpFixture.Reply(body, true))); using var source = new CodexFixtureSource(); var factory = new OpenAiHttpFactory(handler); List<ResponseEvent> events = [];
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => Stream(Provider(factory, source), TestContext.Current.CancellationToken, events.Add));
        if (fault == "late-error") { var native = Assert.IsType<LlmUpstreamException>(exception); Assert.Equal(error, native.ResponseBody); Assert.Equal("text/event-stream", native.ContentType); }
        Assert.DoesNotContain(events, x => x.Type == "response.completed"); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
