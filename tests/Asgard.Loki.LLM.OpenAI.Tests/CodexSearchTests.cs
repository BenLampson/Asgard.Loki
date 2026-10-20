using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.CodexFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>原生辅助搜索保留完整窗口，不推导搜索 schema、模型归属、用量或资料质量。</summary>
public sealed class CodexSearchTests
{
    internal static CodexSearchOptions SearchOptions() => new() { BaseAddress = new(Part("root").GetString()!), Account = Options().Account, TargetId = "native-search", HttpClientName = "search-test" };
    /// <summary>完整调用只有一次原生 HTTP，原始搜索结果顺序、数值、未知数据和 URL 都只作为数据保留。</summary>
    [Fact]
    public async Task NativeSearch_PreservesWholeRequestResponseAndOwnership()
    {
        var bytes = Bytes(Part("search_response").GetRawText()); var body = new OllamaBodyStream(bytes); var parameters = JsonNode.Parse(Part("search_request").GetRawText())!.AsObject(); var before = parameters.ToJsonString();
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Part("search_endpoint").GetString(), message.RequestUri!.OriginalString); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal(Token, message.Headers.Authorization!.Parameter); Assert.Equal(Part("account_id").GetString(), Assert.Single(message.Headers.GetValues("chatgpt-account-id"))); Assert.Equal("application/json", message.Content!.Headers.ContentType!.ToString());
            var wire = JsonSerializer.Deserialize<JsonElement>(await message.Content.ReadAsByteArrayAsync(token)); Assert.True(JsonElement.DeepEquals(Part("search_request"), wire)); Assert.False(wire.TryGetProperty("model", out _)); Assert.False(wire.TryGetProperty("stream", out _)); Assert.Equal("9007199254740993", wire.GetProperty("future").GetProperty("exact").GetRawText()); return OpenAiHttpFixture.Reply(body, false);
        }); using var source = new CodexFixtureSource(); var factory = new OpenAiHttpFactory(handler); var client = new CodexSearchClient(factory, SearchOptions(), Credential(), source, new CodexTestClock()); var result = await client.SearchAsync(parameters, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, result.Body.ToArray()); Assert.Equal(HttpStatusCode.OK, result.StatusCode); Assert.Equal(client.Metadata, result.Metadata); Assert.Equal("external-a", result.Metadata.CredentialId); Assert.Equal(before, parameters.ToJsonString()); Assert.DoesNotContain(Token, JsonSerializer.Serialize(result));
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(source.Disposed); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
        Assert.True(MemoryMarshal.TryGetArray(result.Body, out var array)); array.Array![array.Offset] = 0xff; Assert.Equal(bytes, result.Body.ToArray());
    }
    /// <summary>来源首次等待/工厂之前冻结原生参数，不插入模型/流标志，不执行引用 URL。</summary>
    [Fact]
    public async Task NativeParameters_AreFrozenAndNeverSanitized()
    {
        var parameters = JsonNode.Parse(Part("search_request").GetRawText())!.AsObject(); parameters["model"] = "native-search-model"; parameters["stream"] = true; var expected = parameters.DeepClone();
        using var source = new CodexFixtureSource { Handler = (principal, _) => { parameters.Clear(); return ValueTask.FromResult(new CodexAccessToken(principal, Part("account_id").GetString()!, Token, Now.AddHours(1))); } };
        using var handler = new StubHttpHandler(async (message, token) => { Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token)))); Assert.Equal("application/json", Assert.Single(message.Headers.Accept).MediaType); return CompactionFixture.Json(Part("search_response").GetRawText()); });
        await new CodexSearchClient(new OpenAiHttpFactory(handler), SearchOptions(), Credential(), source, new CodexTestClock()).SearchAsync(parameters, TestContext.Current.CancellationToken); Assert.Empty(parameters); Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls);
    }
    /// <summary>成功窗口是原始 HTTP 数据，允许原生空/非 JSON/二进制/未来错误形状而不假设完整回答。</summary>
    [Theory] [InlineData("empty")] [InlineData("binary")] [InlineData("text")] [InlineData("json-error")] [InlineData("sse")]
    public async Task OpaqueResponse_IsNotProjectedOrInterpreted(string kind)
    {
        var bytes = kind switch { "empty" => [], "binary" => new byte[] { 0xff, 0x80, 0, 0x0d }, "text" => Encoding.Unicode.GetBytes("原生文本"), "sse" => Bytes("data: opaque" + Environment.NewLine + Environment.NewLine), _ => Bytes("{\"error\":{\"opaque_native_data\":true},\"future\":null}") };
        var body = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => { var response = OpenAiHttpFixture.Reply(body, false); if (kind == "empty") response.Content.Headers.ContentType = null; else response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(kind == "binary" ? "application/octet-stream" : kind == "text" ? "text/plain; charset=utf-16" : kind == "sse" ? "text/event-stream" : "application/json"); return Task.FromResult(response); });
        using var source = new CodexFixtureSource(); var result = await new CodexSearchClient(new OpenAiHttpFactory(handler), SearchOptions(), Credential(), source, new CodexTestClock()).SearchAsync(new(), TestContext.Current.CancellationToken); Assert.Equal(bytes, result.Body.ToArray()); if (kind == "empty") Assert.Null(result.ContentType); Assert.Equal(1, handler.Calls);
    }
    /// <summary>非成功 HTTP 的原始错误字节/状态/媒体完整保存，不自动刷新、重试或降级账户。</summary>
    [Theory] [InlineData(400)] [InlineData(401)] [InlineData(429)] [InlineData(503)]
    public async Task HttpError_RetainsRawBodyWithoutRetry(int status)
    {
        byte[] bytes = [0xff, 0x80, 0]; var body = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => { var response = OpenAiHttpFixture.Reply(body, false, status: (HttpStatusCode)status); response.Content.Headers.ContentType = new("application/octet-stream"); return Task.FromResult(response); });
        using var source = new CodexFixtureSource(); var factory = new OpenAiHttpFactory(handler); var exception = await Assert.ThrowsAsync<LlmUpstreamException>(() => new CodexSearchClient(factory, SearchOptions(), Credential(), source, new CodexTestClock()).SearchAsync(new(), TestContext.Current.CancellationToken));
        Assert.Equal(bytes, exception.Body.ToArray()); Assert.Equal((HttpStatusCode)status, exception.StatusCode); Assert.Equal("application/octet-stream", exception.ContentType); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls);
    }
}
