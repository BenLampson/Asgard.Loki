using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Ollama.Tests;

/// <summary>generate 独立终态状态与严格共享读取器的组合边界。</summary>
public sealed class GenerateTerminalTests
{
    internal const string End = "{\"model\":\"native\",\"response\":\"\",\"done\":true}";
    internal const string Partial = "{\"model\":\"native\",\"response\":\"你好🌏\",\"done\":false}";
    /// <summary>不是 Chat 外壳；错误已知字段、截断与终态后数据不能先暴露成功。</summary>
    [Theory] [InlineData("missing")] [InlineData("late")] [InlineData("duplicate")] [InlineData("model")]
    [InlineData("response")] [InlineData("thinking")] [InlineData("reason")] [InlineData("done")] [InlineData("chat")]
    [InlineData("json")] [InlineData("duplicate-json")] [InlineData("unicode")] [InlineData("future")]
    public async Task InvalidGenerate_NeverDeliversTerminal(string kind)
    {
        var raw = kind switch
        {
            "missing" => Partial, "late" => End + "\n" + Partial, "duplicate" => End + "\n" + End,
            "model" => Partial + "\n" + End.Replace("native", "other"), "response" => End.Replace("\"response\":\"\"", "\"response\":null"),
            "thinking" => End[..^1] + ",\"thinking\":false}", "reason" => End[..^1] + ",\"done_reason\":null}", "done" => End.Replace("true", "null"),
            "chat" => OllamaFixture.Terminal, "json" => End + "\n{", "duplicate-json" => End[..^1] + ",\"response\":\"\"}",
            "unicode" => End[..^1] + ",\"future\":\"\\ud800\"}", _ => "{\"type\":\"future\"}"
        };
        var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(raw)); using var handler = Handler(body, true); var factory = new OllamaHttpFactory(handler);
        IOllamaGenerateAdapter provider = new OllamaLlmProvider(factory); var items = new List<OllamaGenerateEvent>();
        var error = await Record.ExceptionAsync(async () => { await foreach (var item in provider.StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken)) items.Add(item); });
        Assert.True(error is InvalidDataException or JsonException, error?.ToString()); Assert.DoesNotContain(items, item => item.IsTerminal);
        Assert.True(body.Disposed); Assert.True(factory.Clients[0].Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>完整 JSON 的残缺/错误结构同样不能误判完成。</summary>
    [Theory] [InlineData("partial")] [InlineData("chat")] [InlineData("array")] [InlineData("thinking")]
    public async Task InvalidCompleteGenerate_IsRejected(string kind)
    {
        var raw = kind switch { "partial" => Partial, "chat" => OllamaFixture.Terminal, "array" => "[]", _ => End[..^1] + ",\"thinking\":null}" };
        var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(raw)); using var handler = Handler(body, false); var provider = new OllamaLlmProvider(new StubHttpClientFactory(handler));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GenerateAsync(Payload(), Context(), TestContext.Current.CancellationToken)); Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>取消与晚到读取/释放故障撤销 generate 终态；不追加 stop 或重试。</summary>
    [Theory] [InlineData(false, "read")] [InlineData(false, "dispose")] [InlineData(false, "eof-cancel")] [InlineData(false, "client-cancel")]
    [InlineData(true, "read")] [InlineData(true, "dispose")] [InlineData(true, "eof-cancel")] [InlineData(true, "client-cancel")]
    public async Task GenerateLateFailure_PreventsSuccess(bool stream, string failure)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(End)) { FailAtEof = failure == "read", FailDispose = failure == "dispose", OnEof = failure == "eof-cancel" ? cancellation.Cancel : null };
        using var handler = Handler(body, stream); var factory = new OllamaHttpFactory(handler) { OnClientDispose = failure == "client-cancel" ? cancellation.Cancel : null }; var provider = new OllamaLlmProvider(factory); var items = new List<OllamaGenerateEvent>();
        var error = await Record.ExceptionAsync(async () =>
        { if (stream) { await foreach (var item in ((IOllamaGenerateAdapter)provider).StreamAsync(Payload(), Context(), cancellation.Token)) items.Add(item); } else await provider.GenerateAsync(Payload(), Context(), cancellation.Token); });
        Assert.NotNull(error); Assert.Empty(items); Assert.True(body.Disposed); Assert.True(factory.Clients[0].Disposed); Assert.Equal(1, handler.Calls);
        if (failure.EndsWith("cancel", StringComparison.Ordinal)) Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.IsAssignableFrom<IOException>(error);
    }
    /// <summary>原生错误在流中作为错误记录，在完整响应中作为保留原文的异常。</summary>
    [Theory] [InlineData(false, 200)] [InlineData(false, 500)] [InlineData(true, 200)] [InlineData(true, 429)]
    public async Task GenerateErrors_RetainNativeBody(bool stream, int status)
    {
        const string raw = "{\"error\":\"generate failed\",\"future\":null}"; var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(raw)); using var handler = Handler(body, stream, status); var provider = new OllamaLlmProvider(new StubHttpClientFactory(handler));
        if (stream && status == 200)
        {
            var items = new List<OllamaGenerateEvent>(); await foreach (var item in ((IOllamaGenerateAdapter)provider).StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken))
            { Assert.True(body.Eof); Assert.True(body.Disposed); items.Add(item); }
            var error = Assert.Single(items); Assert.True(error.IsError); Assert.True(error.IsTerminal); Assert.Null(error.Done); Assert.True(OllamaFixture.Same(raw, error.Data));
        }
        else
        {
            var error = await Assert.ThrowsAsync<LlmUpstreamException>(async () =>
            { if (stream) { await foreach (var _ in ((IOllamaGenerateAdapter)provider).StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken)) { } } else await provider.GenerateAsync(Payload(), Context(), TestContext.Current.CancellationToken); });
            Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.Equal(raw, error.ResponseBody);
        }
        Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>generate 使用完整目标与显式独立协议位。</summary>
    internal static LlmAdapterContext Context() => new(OllamaFixture.Target() with { Protocols = LlmProtocol.OllamaGenerate }, LlmCredential.Anonymous("identity"), "public", "session");
    internal static JsonObject Payload() => JsonNode.Parse(OllamaFixture.Text("generate-request"))!.AsObject();
    /// <summary>直接响应既支持单 JSON 也支持 NDJSON，均不访问网络。</summary>
    internal static StubHttpHandler Handler(Stream body, bool stream, int status = 200) => new((_, _) =>
    { var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new(stream ? "application/x-ndjson" : "application/json"); return Task.FromResult(response); });
}
