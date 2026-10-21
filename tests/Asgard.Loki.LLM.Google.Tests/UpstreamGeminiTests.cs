using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Google.Tests.UpstreamGeminiFixture;

namespace Asgard.Loki.LLM.Google.Tests;

/// <summary>经过真实公共 SDK 的 Gemini 原生完整输入、工具关联、签名和结束边界。</summary>
public sealed class UpstreamGeminiTests
{
    /// <summary>三个完整根分别覆盖原生 JSON 与 SSE。</summary>
    public static IEnumerable<object[]> Cases() { foreach (var profile in new[] { 0, 1, 2 }) foreach (var stream in new[] { false, true }) yield return [profile, stream]; }
    /// <summary>原生字段与事件完整一致，结束帧必须等到 EOF 和全部自有资源关闭。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task WholeNativeWindow_IsPreserved(int profile, bool stream)
    {
        var source = stream ? Window.GetProperty("sse").GetString()! : Window.GetProperty("result").GetRawText(); var bytes = Bytes(source); var body = new OllamaBodyStream(bytes);
        using var handler = new StubHttpHandler(async (message, token) => { Assert.Equal(Profile(profile).GetProperty("root").GetString()+Window.GetProperty(stream ? "stream_path" : "path").GetString(), message.RequestUri!.OriginalString); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal(Profile(profile).GetProperty("key").GetString(), Assert.Single(message.Headers.GetValues("x-goog-api-key"))); Assert.True(JsonElement.DeepEquals(Window.GetProperty("wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, length: bytes.Length); }); var factory = new GoogleHttpFactory(handler); await using var sdk = Build(factory, profile);
        if (stream) { List<GeminiContentEvent> seen = []; await Invoke(sdk, true, seen, item => { if (Ending(item)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } }); var expected = Window.GetProperty("events"); Assert.Equal(expected.GetArrayLength(), seen.Count); for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(expected[i], seen[i].Data)); Assert.Equal("upstream-native", seen[i].EventId); Assert.Equal(0L, seen[i].RetryMilliseconds); Assert.Null(seen[i].EventName); Assert.Equal(Target(profile).UpstreamModel, seen[i].Metadata.UpstreamModel); Assert.Null(seen[i].Metadata.Conversion); } }
        else { var result = await Invoke(sdk); Assert.NotNull(result); Assert.True(JsonElement.DeepEquals(Window.GetProperty("result"), result.Data)); Assert.Equal(Target(profile).UpstreamModel, result.Metadata.UpstreamModel); Assert.Null(result.Metadata.Conversion); }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>真实结束后的读取、关闭取消仍不得交付缓冲终态。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task LateFailure_PreventsEnding(bool stream, bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new OllamaBodyStream(Bytes(stream ? CleanSse : Window.GetProperty("result").GetRawText())) { FailAtEof = !cancel }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, stream))); var factory = new GoogleHttpFactory(handler) { OnDispose = () => { if (cancel) cancellation.Cancel(); } }; await using var sdk = Build(factory); List<GeminiContentEvent> seen = [];
        var failure = await Record.ExceptionAsync(() => Invoke(sdk, stream, seen, token: cancellation.Token)); Assert.NotNull(failure); if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>另一个候选被截断时，已经观察到的首个候选结束也不能提前泄露。</summary>
    [Fact]
    public async Task MissingCandidateTerminal_DiscardsAllEndings()
    {
        var events = Window.GetProperty("events").EnumerateArray().Where(e => !e.TryGetProperty("type", out _)).ToArray(); var source = string.Concat(events.Where((_, i) => i != 3).Select(Frame)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new GoogleHttpFactory(handler); await using var sdk = Build(factory); List<GeminiContentEvent> seen = []; await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, true, seen)); Assert.DoesNotContain(seen, Ending);
    }
    /// <summary>同一个原生生成的 responseId 不得被其他上游响应替换。</summary>
    [Fact]
    public async Task NativeResponseIdentityDrift_IsRejected()
    {
        var events = Window.GetProperty("events").EnumerateArray().Where(e => !e.TryGetProperty("type", out _)).Select(e => JsonNode.Parse(e.GetRawText())!).ToArray(); events[1]["responseId"] = "foreign-response"; var source = string.Concat(events.Select(e => Frame(JsonSerializer.SerializeToElement(e)))); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new GoogleHttpFactory(handler); await using var sdk = Build(factory); List<GeminiContentEvent> seen = []; await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, true, seen)); Assert.DoesNotContain(seen, Ending);
    }
    /// <summary>原始 HTTP 错误包括二进制字节，不可经文本替换再编码。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task BinaryHttpError_IsOriginal(bool stream)
    {
        var bytes = new byte[] { 0, 255, 128, 65 }; var body = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, false, HttpStatusCode.TooManyRequests, bytes.Length))); var factory = new GoogleHttpFactory(handler); await using var sdk = Build(factory); var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, stream, [])); Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode); Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal("application/json", error.ContentType); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>有效终态之前的非法 UTF-8 注释不能被替换并静默跳过。</summary>
    [Fact]
    public async Task InvalidUtf8Comment_IsRejected()
    {
        var bytes = new byte[] { 58, 32, 255, 10, 10 }.Concat(Bytes(CleanSse)).ToArray(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(bytes), true))); var factory = new GoogleHttpFactory(handler); await using var sdk = Build(factory); List<GeminiContentEvent> seen = []; await Assert.ThrowsAnyAsync<InvalidDataException>(() => Invoke(sdk, true, seen)); Assert.DoesNotContain(seen, Ending);
    }
}
