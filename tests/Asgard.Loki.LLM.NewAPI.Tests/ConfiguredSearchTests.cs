using System.Net;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.NewAPI.Tests.ConfiguredSearchFixture;

namespace Asgard.Loki.LLM.NewAPI.Tests;

/// <summary>明确配置下完整原生搜索参数、固定目标、认证和原始 HTTP 字节保真。</summary>
public sealed class ConfiguredSearchTests
{
    /// <summary>四种明确认证分别发送完整原生参数，实际查询凭据不进入返回地址和元数据。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task WholeNativeWindowAndSafeMetadata_ArePreserved(int profile)
    {
        var config = Profile(profile); var options = Options(profile); var parameters = Parameters(); var before = parameters.ToJsonString(); var bytes = SearchFixture.Bytes(Window.GetProperty("responses")[0]); var body = new OllamaBodyStream(bytes);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Window.GetProperty("root").GetString()+Window.GetProperty("path").GetString()+config.GetProperty("query_suffix").GetString(), message.RequestUri!.OriginalString); Assert.Equal("configured.invalid", message.Headers.Host); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("application/json", message.Content!.Headers.ContentType!.ToString()); Assert.Equal("application/json", Assert.Single(message.Headers.Accept).MediaType);
            foreach (var name in new[] { "Authorization", "x-search-key", "x-api-key", "x-goog-api-key", "api-key" }) { if (name == config.GetProperty("header").GetString()) Assert.Equal(config.GetProperty("value").GetString(), Assert.Single(message.Headers.NonValidated[name])); else Assert.False(message.Headers.Contains(name)); }
            Assert.Equal("kept", Assert.Single(message.Headers.GetValues("x-caller-context"))); var expected = parameters.DeepClone().AsObject(); expected["model"] = Window.GetProperty("model").GetString(); Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), JsonSerializer.Deserialize<JsonElement>(await message.Content.ReadAsByteArrayAsync(token)))); return SearchFixture.Reply(body, length: bytes.Length);
        }); using var factory = new SearchFactory(handler) { Configure = client => { client.BaseAddress = new("https://unused.invalid/"); client.DefaultRequestHeaders.Host = "unused.invalid"; client.DefaultRequestHeaders.Add("x-caller-context", "kept"); if (config.GetProperty("header").GetString() is { } name) client.DefaultRequestHeaders.TryAddWithoutValidation(name, "wrong-default"); } }; var client = new NewApiSearchClient(factory, options, Credential(profile)); var result = await client.SearchAsync(parameters, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, result.Body.ToArray()); Assert.Equal(HttpStatusCode.OK, result.StatusCode); Assert.Equal(before, parameters.ToJsonString()); Assert.Same(client.Metadata, result.Metadata); Assert.Equal(Window.GetProperty("root").GetString()+Window.GetProperty("path").GetString(), result.Endpoint.OriginalString); Assert.Equal(options.Authentication, result.Metadata.Authentication); Assert.Equal(options.Transport, result.Metadata.Transport); Assert.Equal(options.UpstreamModel, result.Metadata.UpstreamModel); Assert.Equal("configured-key", result.Metadata.CredentialId); Assert.Equal(options.Version, result.Metadata.Version);
        if (profile != 1) { var key = config.GetProperty("key").GetString()!; Assert.DoesNotContain(key, JsonSerializer.Serialize(client.Metadata)); Assert.DoesNotContain(key, result.Endpoint.OriginalString); Assert.DoesNotContain(key, client.ToString()!); } if (profile == 3) Assert.DoesNotContain(config.GetProperty("query_suffix").GetString()!, result.Endpoint.OriginalString); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>已有原生字节类型在显式查询鉴权下仍保持不透明。</summary>
    public static IEnumerable<object[]> Responses() => Window.GetProperty("responses").EnumerateArray().Select(item => new object[] { item.GetProperty("name").GetString()! });
    /// <summary>JSON、SSE、文本、空响应和二进制不合成状态或修复参数。</summary>
    [Theory] [MemberData(nameof(Responses))]
    public async Task AllOpaqueWindows_RemainOriginal(string name)
    {
        var item = Window.GetProperty("responses").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name); var bytes = SearchFixture.Bytes(item); using var handler = new StubHttpHandler((_, _) => Task.FromResult(SearchFixture.Reply(new OllamaBodyStream(bytes), item.GetProperty("status").GetInt32(), item.GetProperty("type").GetString(), bytes.Length))); using var factory = new SearchFactory(handler); var result = await new NewApiSearchClient(factory, Options(3), Credential(3)).SearchAsync(Parameters(), TestContext.Current.CancellationToken); Assert.Equal(bytes, result.Body.ToArray()); Assert.Equal((HttpStatusCode)item.GetProperty("status").GetInt32(), result.StatusCode); Assert.Equal(item.GetProperty("type").GetString(), result.ContentType); Assert.Equal(1, handler.Calls); Assert.False(factory.Disposed);
    }
    /// <summary>读取末尾、内容关闭和客户端关闭的失败不得变成完整搜索结果。</summary>
    [Theory] [InlineData("read")] [InlineData("content")] [InlineData("cancel")]
    public async Task LateFailure_PreventsDelivery(string stage)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new OllamaBodyStream(SearchFixture.Bytes(Window.GetProperty("responses")[0])) { FailAtEof = stage == "read" }; var content = new SearchContent(body) { FailDispose = stage == "content" }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })); using var factory = new SearchFactory(handler) { OnDispose = () => { if (stage == "cancel") cancellation.Cancel(); } }; var client = new NewApiSearchClient(factory, Options(3), Credential(3)); var failure = await Record.ExceptionAsync(() => client.SearchAsync(Parameters(), cancellation.Token)); Assert.NotNull(failure); if (stage == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生失败状态、实际媒体和任意错误字节保持原文且不重试。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task NativeHttpErrors_RemainOriginal(int index)
    {
        var item = Window.GetProperty("errors")[index]; var bytes = SearchFixture.Bytes(item); using var handler = new StubHttpHandler((_, _) => Task.FromResult(SearchFixture.Reply(new OllamaBodyStream(bytes), item.GetProperty("status").GetInt32(), item.GetProperty("type").GetString(), bytes.Length))); using var factory = new SearchFactory(handler); var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => new NewApiSearchClient(factory, Options(3), Credential(3)).SearchAsync(Parameters(), TestContext.Current.CancellationToken)); Assert.Equal(bytes, failure.Body.ToArray()); Assert.Equal((HttpStatusCode)item.GetProperty("status").GetInt32(), failure.StatusCode); Assert.Equal(item.GetProperty("type").GetString(), failure.ContentType); Assert.Equal(1, handler.Calls);
    }
    /// <summary>同一固定实例冻结参数后才调用工厂，后续重复调用保持同一元数据但不持有历史。</summary>
    [Fact]
    public async Task SnapshotAndRepeatedCalls_KeepFixedNativeTarget()
    {
        var parameters = Parameters(); var expected = parameters.DeepClone().AsObject(); expected["model"] = Window.GetProperty("model").GetString(); using var handler = new StubHttpHandler(async (message, token) => { Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return SearchFixture.Reply(new OllamaBodyStream([])); }); using var factory = new SearchFactory(handler) { Configure = _ => parameters.Clear() }; var client = new NewApiSearchClient(factory, Options(1), Credential(1)); var first = await client.SearchAsync(parameters, TestContext.Current.CancellationToken); var second = await client.SearchAsync(Parameters(), TestContext.Current.CancellationToken); Assert.Same(first.Metadata, second.Metadata); Assert.Equal(2, handler.Calls); Assert.Empty(parameters); Assert.False(factory.Disposed);
    }
}
