using System.Net;
using System.Text.Json;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Google.Tests.UpstreamGeminiFixture;

namespace Asgard.Loki.LLM.Google.Tests;

/// <summary>新传输策略的精确预算、完整 Session 绑定及原生错误边界。</summary>
public sealed class GoogleHttpPolicyTests
{
    private static ILokiClient Configured(GoogleHttpFactory factory, GoogleHttpOptions options, ILlmSessionStore? store = null)
        => new LokiClientBuilder().AddLLM(new LlmOptions { Providers = [new GoogleLlmProvider(factory, options, id: "native-gemini-upstream", httpClientName: Profile().GetProperty("client").GetString()!)], Credentials = [new("native-key", Profile().GetProperty("key").GetString()!)], SessionStore = store ?? Store(), Routes = [new() { Model = "public", Targets = [Target()] }] }).Build();
    private static GoogleHttpOptions Policy(string field, int value) => field switch { "request" => new() { MaximumRequestBytes = value }, "body" => new() { MaximumBodyBytes = value }, "tail" => new() { MaximumBufferedTerminalEvents = value }, _ => new() { MaximumTrackedCandidates = value } };
    /// <summary>每个资源策略必须为正，不能以零表示无限制。</summary>
    [Theory] [InlineData("request")] [InlineData("body")] [InlineData("tail")] [InlineData("candidates")]
    public void ZeroPolicy_IsRejected(string field)
    { using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); var factory = new GoogleHttpFactory(handler); Assert.Throws<ArgumentOutOfRangeException>(() => new GoogleLlmProvider(factory, Policy(field, 0))); Assert.Empty(factory.Clients); }
    /// <summary>实现策略变更属于完整会话绑定，在第二次发送前明确失败。</summary>
    [Theory] [InlineData("request")] [InlineData("body")] [InlineData("tail")] [InlineData("candidates")]
    public async Task PolicyDrift_IsRejected(string field)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Window.GetProperty("result").GetRawText())))));
        var factory = new GoogleHttpFactory(handler); var store = Store(); await using (var first = Build(factory, store: store)) await Invoke(first); await using var second = Configured(factory, Policy(field, 7), store); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(second)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>同名公开模型下完整地址、实际模型、Key、客户端和配置版本仍不能漂移。</summary>
    [Theory] [InlineData("root")] [InlineData("model")] [InlineData("key")] [InlineData("client")] [InlineData("version")]
    public async Task FullTargetDrift_IsRejected(string field)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Window.GetProperty("result").GetRawText()))))); var factory = new GoogleHttpFactory(handler); var store = Store(); await using (var first = Build(factory, store: store)) await Invoke(first);
        var target = Target(); target = field switch { "root" => target with { BaseAddress = Target(1).BaseAddress }, "model" => target with { UpstreamModel = "other" }, "version" => target with { Version = "changed" }, _ => target };
        await using var second = Build(factory, store: store, target: target, key: field == "key" ? "other" : null, client: field == "client" ? "Explicit.Changed" : null); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(second)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>旧构造入口与显式默认策略可恢复同一完整绑定，原生历史不被隐藏拼接。</summary>
    [Fact]
    public async Task SameBinding_ReplaysOriginalHistory()
    {
        using var handler = new StubHttpHandler(async (message, token) => { Assert.True(JsonElement.DeepEquals(Window.GetProperty("wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Window.GetProperty("result").GetRawText()))); }); var factory = new GoogleHttpFactory(handler); var store = Store(); await using (var first = Build(factory, store: store)) await Invoke(first); await using (var second = Configured(factory, new(), store)) await Invoke(second); Assert.Equal(2, handler.Calls);
    }
    /// <summary>尾部数量与候选关联数按实际条目计数，精确上限通过，小一条必须撤回终态。</summary>
    [Theory] [InlineData("tail", false)] [InlineData("tail", true)] [InlineData("candidates", false)] [InlineData("candidates", true)]
    public async Task EntryBudgets_AreExact(string field, bool exact)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(CleanSse)), true))); var factory = new GoogleHttpFactory(handler); await using var sdk = Configured(factory, Policy(field, (field == "tail" ? 3 : 2)-(exact ? 0 : 1))); List<GeminiContentEvent> seen = []; if (exact) await Invoke(sdk, true, seen); else { await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, true, seen)); Assert.DoesNotContain(seen, Ending); } Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>请求与全部 SSE 字节均计入预算，末尾控制、注释和分隔不能绕过限制。</summary>
    [Theory] [InlineData("request", false)] [InlineData("request", true)] [InlineData("body", false)] [InlineData("body", true)]
    public async Task ByteBudgets_AreExact(string field, bool exact)
    {
        var size = Bytes(field == "request" ? JsonSerializer.Serialize(Window.GetProperty("wire")) : CleanSse).Length; var body = new OllamaBodyStream(Bytes(CleanSse)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new GoogleHttpFactory(handler); await using var sdk = Configured(factory, Policy(field, size-(exact ? 0 : 1))); List<GeminiContentEvent> seen = []; if (exact) await Invoke(sdk, true, seen); else { await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, true, seen)); Assert.DoesNotContain(seen, Ending); } Assert.Equal(field == "request" && !exact ? 0 : 1, handler.Calls);
    }
    /// <summary>请求声明长度不匹配会使整个 JSON/SSE 失败，结束不能提前交付。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task DeclaredLength_MustMatch(bool stream, bool longer)
    {
        var bytes = Bytes(stream ? CleanSse : Window.GetProperty("result").GetRawText()); var body = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, stream, length: bytes.Length+(longer ? 1 : -1)))); var factory = new GoogleHttpFactory(handler); await using var sdk = Build(factory); List<GeminiContentEvent> seen = []; await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, stream, seen)); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>借用客户端不能以另一种认证改变实际 Key 身份，也不能携带签名残留。</summary>
    [Theory] [InlineData("Authorization")] [InlineData("x-api-key")] [InlineData("api-key")] [InlineData("x-amz-security-token")]
    public async Task UnselectedCredentialDefault_IsRejected(string header)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); var factory = new GoogleHttpFactory(handler) { Configure = client => client.DefaultRequestHeaders.TryAddWithoutValidation(header, "unrelated") }; await using var sdk = Build(factory); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(sdk)); Assert.Equal(0, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>后续原生错误撤回已缓存的候选终态，异常保持实际错误负载。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LateNativeFailure_DiscardsEnding(bool block)
    {
        var error = Window.GetProperty(block ? "blocked" : "error"); var original = JsonSerializer.Serialize(error); var source = CleanSse+Frame(error); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new GoogleHttpFactory(handler); await using var sdk = Build(factory); List<GeminiContentEvent> seen = []; var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, true, seen)); Assert.Equal(original, failure.ResponseBody); Assert.DoesNotContain(seen, Ending); Assert.Equal(1, handler.Calls);
    }
    /// <summary>首个原生错误或阻断仍作为原始协议数据，等待 EOF 与关闭，不伪造候选。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeFailure_IsOriginal(bool block)
    {
        var error = Window.GetProperty(block ? "blocked" : "error"); var body = new OllamaBodyStream(Bytes(Frame(error))); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new GoogleHttpFactory(handler); await using var sdk = Build(factory); List<GeminiContentEvent> seen = []; await Invoke(sdk, true, seen, _ => { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); }); Assert.True(JsonElement.DeepEquals(error, Assert.Single(seen).Data));
    }
    /// <summary>自有正文或客户端关闭失败，不能返回已读完的 JSON 或结束帧。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task DisposalFailure_PreventsDelivery(bool stream, bool clientFault)
    {
        var body = new OllamaBodyStream(Bytes(stream ? CleanSse : Window.GetProperty("result").GetRawText())) { FailDispose = !clientFault }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, stream))); var factory = new GoogleHttpFactory(handler) { OnDispose = () => { if (clientFault) throw new IOException("client close"); } }; await using var sdk = Build(factory); List<GeminiContentEvent> seen = []; await Assert.ThrowsAsync<IOException>(() => Invoke(sdk, stream, seen)); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>新增未知 type 字段不能屏蔽同帧已知候选/用量载体，所有扩展值仍保持原文。</summary>
    [Fact]
    public async Task UnknownType_DoesNotHideNativeCarrier()
    {
        var events = Window.GetProperty("events").EnumerateArray().Where(e => !e.TryGetProperty("type", out _)).Select(e => System.Text.Json.Nodes.JsonNode.Parse(e.GetRawText())!).ToArray();
        foreach (var item in events) item["type"] = "future.native-extension";
        var source = string.Concat(events.Select(e => Frame(JsonSerializer.SerializeToElement(e)))); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new GoogleHttpFactory(handler); await using var sdk = Build(factory); List<GeminiContentEvent> seen = []; await Invoke(sdk, true, seen); Assert.Equal(events.Length, seen.Count); for (var i = 0; i < seen.Count; i++) Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(events[i]), seen[i].Data));
    }
    /// <summary>消费者提前退出只释放当前 HTTP 调用，不补流、重试或读取媒体引用。</summary>
    [Fact]
    public async Task EarlyExit_ReleasesCall()
    {
        var body = new OllamaBodyStream(Bytes(CleanSse)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new GoogleHttpFactory(handler); await using var sdk = Build(factory); await foreach (var item in sdk.LLM.Gemini.StreamAsync(Request(), new() { SessionId = "native-gemini" }, TestContext.Current.CancellationToken)) { Assert.False(Ending(item)); break; } Assert.False(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
