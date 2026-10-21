using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.NewAPI.Tests.SearchFixture;

namespace Asgard.Loki.LLM.NewAPI.Tests;

/// <summary>原生 API Key 搜索完整目标、未知参数和原始 HTTP 字节窗口。</summary>
public sealed class SearchTests
{
    /// <summary>独立目标分别覆盖四种 id/stream 原生参数模式。</summary>
    public static IEnumerable<object[]> Cases() { foreach (var profile in new[] { 0, 1, 2 }) foreach (var kind in new[] { "omitted", "null", "false", "true" }) yield return [profile, kind]; }
    /// <summary>完整参数、固定 model 和独立 Key 正确发送，不受借用客户端默认地址和认证影响。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task WholeNativeWindow_IsPreserved(int profile, string kind)
    {
        var parameters = Parameters(kind); var before = parameters.ToJsonString(); var item = Window.GetProperty("responses")[0]; var bytes = Bytes(item); var body = new OllamaBodyStream(bytes); HttpContent? owned = null;
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Profile(profile).GetProperty("root").GetString()+"alpha/search", message.RequestUri!.OriginalString); Assert.Equal(message.RequestUri.Authority, message.Headers.Host); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(Profile(profile).GetProperty("key").GetString(), message.Headers.Authorization.Parameter); Assert.Equal("application/json", Assert.Single(message.Headers.Accept).MediaType); Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            owned = message.Content; var actual = JsonSerializer.Deserialize<JsonElement>(await message.Content.ReadAsByteArrayAsync(token)); OriginalRequest(actual, parameters, profile); Assert.Equal("9007199254740993", actual.GetProperty("native_extra").GetProperty("exact").GetRawText()); return Reply(body, type: item.GetProperty("type").GetString(), length: bytes.Length);
        }); using var factory = new SearchFactory(handler) { Configure = client => { client.BaseAddress = new("https://unrelated.invalid/"); client.DefaultRequestHeaders.Authorization = new("Bearer", "wrong-default-key"); client.DefaultRequestHeaders.Host = "wrong.invalid"; } }; var options = Options(profile); if (profile == 2) options = options with { BaseAddress = new(options.BaseAddress.AbsoluteUri.TrimEnd('/')) }; var client = new NewApiSearchClient(factory, options, Credential(profile)); var result = await client.SearchAsync(parameters, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, result.Body.ToArray()); Assert.Equal(HttpStatusCode.OK, result.StatusCode); Assert.Equal(item.GetProperty("type").GetString(), result.ContentType); Assert.Equal(client.Metadata, result.Metadata); Assert.Equal(options.UpstreamModel, result.Metadata.UpstreamModel); Assert.Equal(options.Version, result.Metadata.Version); Assert.Equal("credential-"+profile, result.Metadata.CredentialId); Assert.Equal(Profile(profile).GetProperty("root").GetString(), result.Metadata.BaseAddress.OriginalString); Assert.Equal(Profile(profile).GetProperty("root").GetString()+"alpha/search", result.Endpoint.OriginalString); Assert.Equal(before, parameters.ToJsonString()); Assert.Equal(options.HttpClientName, Assert.Single(factory.Names)); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls); Assert.NotNull(owned); await Assert.ThrowsAsync<ObjectDisposedException>(() => owned.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }
    /// <summary>返回内容可为 JSON、文本、二进制、空正文或 SSE，不猜测资料结构或完成状态。</summary>
    public static IEnumerable<object[]> Responses() => Window.GetProperty("responses").EnumerateArray().Select(item => new object[] { item.GetProperty("name").GetString()! });
    /// <summary>原始字节与媒体类型独立保存；调用者修改返回内存副本不会污染结果。</summary>
    [Theory] [MemberData(nameof(Responses))]
    public async Task OpaqueResponse_IsOriginal(string name)
    {
        var item = Window.GetProperty("responses").EnumerateArray().Single(i => i.GetProperty("name").GetString() == name); var bytes = Bytes(item); var body = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, item.GetProperty("status").GetInt32(), item.GetProperty("type").GetString(), bytes.Length))); using var factory = new SearchFactory(handler); var client = new NewApiSearchClient(factory, Options(), Credential()); var result = await client.SearchAsync(Parameters("true"), TestContext.Current.CancellationToken);
        Assert.Equal(bytes, result.Body.ToArray()); Assert.Equal((HttpStatusCode)item.GetProperty("status").GetInt32(), result.StatusCode); Assert.Equal(item.GetProperty("type").GetString(), result.ContentType); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
        if (bytes.Length > 0) { Assert.True(MemoryMarshal.TryGetArray(result.Body, out var copy)); copy.Array![copy.Offset] ^= 0xff; Assert.Equal(bytes, result.Body.ToArray()); } Assert.DoesNotContain(Credential().ApiKey, JsonSerializer.Serialize(client.Metadata)); Assert.DoesNotContain(Credential().ApiKey, client.ToString()!);
    }
    /// <summary>每个固定 Key 分别保留四种原生 HTTP 错误。</summary>
    public static IEnumerable<object[]> Errors() { foreach (var profile in new[] { 0, 1, 2 }) for (var error = 0; error < 4; error++) yield return [profile, error]; }
    /// <summary>异常保留原始状态、任意字节和媒体类型；不重试或使用其他目标。</summary>
    [Theory] [MemberData(nameof(Errors))]
    public async Task NativeHttpError_IsOriginal(int profile, int error)
    {
        var item = Window.GetProperty("errors")[error]; var bytes = Bytes(item); var body = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, item.GetProperty("status").GetInt32(), item.GetProperty("type").GetString(), bytes.Length))); using var factory = new SearchFactory(handler); var client = new NewApiSearchClient(factory, Options(profile), Credential(profile)); var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => client.SearchAsync(Parameters(), TestContext.Current.CancellationToken));
        Assert.Equal((HttpStatusCode)item.GetProperty("status").GetInt32(), failure.StatusCode); Assert.Equal(bytes, failure.Body.ToArray()); Assert.Equal(item.GetProperty("type").GetString(), failure.ContentType); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>在借用工厂回调之前冻结全部原生字段；外部随后修改原参数不改变已选请求。</summary>
    [Fact]
    public async Task Snapshot_PrecedesFactory()
    {
        var parameters = Parameters("null"); var expected = parameters.DeepClone().AsObject(); using var handler = new StubHttpHandler(async (message, token) => { OriginalRequest(JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)), expected); return Reply(new OllamaBodyStream([])); }); using var factory = new SearchFactory(handler) { Configure = _ => parameters.Clear() }; await new NewApiSearchClient(factory, Options(), Credential()).SearchAsync(parameters, TestContext.Current.CancellationToken); Assert.Empty(parameters); Assert.Equal(1, handler.Calls);
    }
    /// <summary>服务端校验可拒绝原生值；SDK 不修复 stream 或补造 query。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RejectedNativeParameters_AreNotRepaired(bool invalidStream)
    {
        var parameters = new JsonObject(); if (invalidStream) parameters["stream"] = "native-invalid-value"; using var handler = new StubHttpHandler(async (message, token) => { OriginalRequest(JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)), parameters); return Reply(new OllamaBodyStream(Bytes(Window.GetProperty("errors")[0])), 400); }); using var factory = new SearchFactory(handler); await Assert.ThrowsAsync<LlmUpstreamException>(() => new NewApiSearchClient(factory, Options(), Credential()).SearchAsync(parameters, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>复用同一固定目标只产生独立调用，不修改标识、不追加历史或释放借用工厂。</summary>
    [Fact]
    public async Task RepeatedCalls_KeepFixedTarget()
    {
        using var handler = new StubHttpHandler(async (message, token) => { OriginalRequest(JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)), Parameters()); return Reply(new OllamaBodyStream([])); }); using var factory = new SearchFactory(handler); var client = new NewApiSearchClient(factory, Options(), Credential()); for (var i = 0; i < 2; i++) { var result = await client.SearchAsync(Parameters(), TestContext.Current.CancellationToken); Assert.Same(client.Metadata, result.Metadata); } Assert.Equal(2, handler.Calls); Assert.All(factory.Clients, client => Assert.True(client.Disposed)); Assert.False(factory.Disposed);
    }
}
