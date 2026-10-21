using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.DeepSeek.Tests;

/// <summary>只通过供应商公开原生 SDK 调用，比对整个请求和响应，不执行搜索结果或页面内容。</summary>
public sealed class NativeToolsTests
{
    /// <summary>完整和最小窗口保留原生字段、零/空/null/false、结果顺序及重复 URL，方法不注入模型或默认值。</summary>
    [Theory] [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)] [InlineData(true, 0)] [InlineData(true, 1)]
    public async Task NativeGoldenMatchesWholeJsonAndFixedEndpoint(bool fetch, int index)
    {
        using var factory = ToolsFixture.Factory(fetch, index); var expected = ToolsFixture.Response(fetch, index); using var stream = new ToolsReadStream(Encoding.UTF8.GetBytes(expected.GetRawText()));
        factory.OnSend = async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal(ToolsFixture.Part("endpoints").GetProperty(fetch ? "fetch" : "search").GetString(), request.RequestUri!.AbsoluteUri); Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal(ToolsFixture.Key, request.Headers.Authorization.Parameter); Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType); var raw = ToolsFixture.Element(await request.Content.ReadAsStringAsync(token)); Assert.True(JsonElement.DeepEquals(ToolsFixture.Part(fetch ? "fetch_requests" : "search_requests")[index], raw)); Assert.False(raw.TryGetProperty("model", out _)); Assert.False(raw.TryGetProperty("stream", out _));
            return new(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        };
        var client = new DeepSeekToolsClient(factory, ToolsFixture.Options()); var body = ToolsFixture.Request(fetch, index); var before = body.ToJsonString(); var result = await ToolsFixture.Invoke(client, fetch, TestContext.Current.CancellationToken, body);
        Assert.True(JsonElement.DeepEquals(expected, result.Data)); Assert.Equal(before, body.ToJsonString()); Assert.Equal(fetch ? "fetch" : "search", result.Operation); Assert.Equal(ToolsFixture.Part("endpoints").GetProperty(result.Operation).GetString(), result.Endpoint.AbsoluteUri); Assert.Equal(HttpStatusCode.OK, result.StatusCode); Assert.Null(result.ContentType); Assert.Equal("fixture-deepseek-tools", factory.ClientName); Assert.True(stream.Eof); Assert.True(stream.Disposed); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Creates); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
        if (!fetch && index == 0) { Assert.Equal(0, result.Data.GetProperty("results")[0].GetProperty("rerank_score").GetInt32()); Assert.Equal(2.875, result.Data.GetProperty("results")[1].GetProperty("rerank_score").GetDouble()); Assert.Equal(result.Data.GetProperty("results")[0].GetProperty("url").GetString(), result.Data.GetProperty("results")[1].GetProperty("url").GetString()); Assert.Equal("9007199254740993", result.Data.GetProperty("future").GetProperty("exact").GetRawText()); }
    }
    /// <summary>调用期间外部 DOM 被清空也不改变已冻结的原生正文。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RequestSnapshotPrecedesTransportAwait(bool fetch)
    {
        var payload = ToolsFixture.Request(fetch); using var factory = ToolsFixture.Factory(fetch); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); factory.OnSend = async (request, token) =>
        { entered.SetResult(); await release.Task.WaitAsync(token); Assert.True(JsonElement.DeepEquals(ToolsFixture.Part(fetch ? "fetch_requests" : "search_requests")[0], ToolsFixture.Element(await request.Content!.ReadAsStringAsync(token)))); return ToolsFixture.Json(ToolsFixture.Response(fetch)); };
        var pending = ToolsFixture.Invoke(new(factory, ToolsFixture.Options()), fetch, TestContext.Current.CancellationToken, payload); await entered.Task.WaitAsync(TestContext.Current.CancellationToken); payload.Clear(); release.SetResult(); var result = await pending; Assert.Empty(payload); Assert.True(JsonElement.DeepEquals(ToolsFixture.Response(fetch), result.Data)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>输入页面甚至指向本地地址，也只能作为原生 JSON 字段交给已配置工具服务；无本地抓取或结果补取。</summary>
    [Fact]
    public async Task FetchUrlDoesNotSelectSdkHttpDestination()
    {
        using var factory = ToolsFixture.Factory(true); const string page = "http://127.0.0.1:1/private?q=a#keep"; factory.OnSend = async (request, token) =>
        { Assert.Equal("https://tools.example.invalid/proxy/v1/tools/fetch", request.RequestUri!.AbsoluteUri); Assert.Equal(page, ToolsFixture.Element(await request.Content!.ReadAsStringAsync(token)).GetProperty("url").GetString()); return ToolsFixture.Json(ToolsFixture.Response(true)); };
        var result = await new DeepSeekToolsClient(factory, ToolsFixture.Options()).FetchAsync(new() { ["url"] = page }, TestContext.Current.CancellationToken); Assert.Equal("fetch", result.Operation); Assert.Equal(1, factory.Calls);
    }
    /// <summary>native code/message 和业务 model 字段不是模型路由或错误，完整原文保留。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnknownBusinessFieldsRemainNative(bool fetch)
    {
        using var factory = ToolsFixture.Factory(fetch); var payload = ToolsFixture.Request(fetch); payload["model"] = "business-model"; var resultJson = JsonNode.Parse(ToolsFixture.Response(fetch).GetRawText())!.AsObject(); resultJson["code"] = "business-code"; resultJson["message"] = "business-message";
        factory.OnSend = async (request, token) => { Assert.Equal("business-model", ToolsFixture.Element(await request.Content!.ReadAsStringAsync(token)).GetProperty("model").GetString()); return ToolsFixture.Json(ToolsFixture.Element(resultJson.ToJsonString())); };
        var result = await ToolsFixture.Invoke(new(factory, ToolsFixture.Options()), fetch, TestContext.Current.CancellationToken, payload); Assert.True(JsonElement.DeepEquals(ToolsFixture.Element(resultJson.ToJsonString()), result.Data)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>同一借用工厂上的并发客户端仍固定各自服务和凭据，每个显式操作只发生一次。</summary>
    [Fact]
    public async Task ConcurrentClientsRetainTheirOwnTargetAndCredential()
    {
        using var factory = ToolsFixture.Factory(); factory.OnSend = async (request, token) =>
        {
            await Task.Yield(); token.ThrowIfCancellationRequested(); var host = request.RequestUri!.Host; Assert.Equal(host == "a.invalid" ? "fixture-key-a" : "fixture-key-b", request.Headers.Authorization!.Parameter);
            return ToolsFixture.Json(ToolsFixture.Response(request.RequestUri.AbsolutePath.EndsWith("/fetch", StringComparison.Ordinal)));
        };
        var a = new DeepSeekToolsClient(factory, ToolsFixture.Options(root: "https://a.invalid/tenant/v1/tools/", key: "fixture-key-a")); var b = new DeepSeekToolsClient(factory, ToolsFixture.Options(root: "https://b.invalid/tenant/v1/tools/", key: "fixture-key-b"));
        var operations = Enumerable.Range(0, 8).Select(index => Task.Run(() => ToolsFixture.Invoke(index % 2 == 0 ? a : b, index % 2 != 0, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        var results = await Task.WhenAll(operations); Assert.Equal(4, results.Count(r => r.Endpoint.Host == "a.invalid" && r.Operation == "search")); Assert.Equal(4, results.Count(r => r.Endpoint.Host == "b.invalid" && r.Operation == "fetch")); Assert.Equal(8, factory.Calls); Assert.Equal(8, factory.Creates); Assert.Equal(8, factory.Disposals); Assert.False(factory.Disposed);
    }

}
