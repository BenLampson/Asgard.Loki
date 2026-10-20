using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;
using static Asgard.Loki.LLM.OpenAI.Tests.BaiduV2Fixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>千帆原生请求头、工具、搜索与过滤元数据的完整离线协议窗口。</summary>
public sealed class BaiduV2CompatibleTests
{
    /// <summary>无 appid、显式 appid 及代理根分别验证五种原生 JSON/SSE。</summary>
    public static IEnumerable<object[]> Cases()
    { foreach (var profile in new[] { 0, 1, 2 }) foreach (var mode in new[] { "reasoning", "tools", "schema", "search", "filtered" }) foreach (var stream in new[] { false, true }) yield return [profile, mode, stream]; }
    /// <summary>逐字段比较完整原文，终态仅在 EOF 和资源释放后交付。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task WholeNativeWindows_ArePreserved(int profile, string mode, bool stream)
    {
        var part = Part(mode); var source = stream ? part.GetProperty("sse").GetString()! : part.GetProperty("result").GetRawText(); var bytes = Bytes(source); var body = new OllamaBodyStream(bytes); var target = Target(mode, profile);
        if (profile == 2) target = target with { BaseAddress = new(target.BaseAddress.AbsoluteUri.TrimEnd('/')) };
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Profile(profile).GetProperty("root").GetString()+Window.GetProperty("path").GetString(), message.RequestUri!.OriginalString); Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(Profile(profile).GetProperty("key").GetString(), message.Headers.Authorization.Parameter);
            if (profile == 0) Assert.False(message.Headers.Contains("appid")); else Assert.Equal(Profile(profile).GetProperty("appid").GetString(), Assert.Single(message.Headers.GetValues("appid")));
            Assert.True(JsonElement.DeepEquals(part.GetProperty(stream ? "wire_stream" : "wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, bytes.Length);
        });
        var factory = Factory(handler, profile); await using var sdk = Build(factory, mode, profile, target: target);
        if (stream)
        {
            List<ChatCompletionEvent> seen = []; await Invoke(sdk, mode, true, seen, item => { if (Ending(item)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } });
            var expected = part.GetProperty("events"); Assert.Equal(expected.GetArrayLength(), seen.Count);
            for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(expected[i], seen[i].Data)); Assert.Equal("baidu-transport", seen[i].EventId); Assert.Equal(0L, seen[i].RetryMilliseconds); Assert.Null(seen[i].EventName); Assert.Equal(target.UpstreamModel, seen[i].Metadata.UpstreamModel); Assert.Null(seen[i].Metadata.Conversion); }
        }
        else { var result = await Invoke(sdk, mode); Assert.NotNull(result); Assert.True(JsonElement.DeepEquals(part.GetProperty("result"), result.Data)); Assert.Equal(part.GetProperty("result").GetProperty("model").GetString(), result.Data.GetProperty("model").GetString()); Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); Assert.Null(result.Metadata.Conversion); }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>appid 属于调用者借用工厂的固定命名客户端配置，不解析凭据字符串。</summary>
    internal static OpenAiHttpFactory Factory(HttpMessageHandler handler, int profile)
        => new(handler) { Configure = client => { var appid = Profile(profile).GetProperty("appid"); if (appid.ValueKind != JsonValueKind.Null) client.DefaultRequestHeaders.Add("appid", appid.GetString()); } };
    /// <summary>原生错误完整保留根级 code/message/type 和 UTF-8 正文，不重试。</summary>
    [Theory] [InlineData(false, 400)] [InlineData(true, 400)] [InlineData(false, 401)] [InlineData(true, 401)] [InlineData(false, 429)] [InlineData(true, 429)] [InlineData(false, 503)] [InlineData(true, 503)]
    public async Task NativeHttpError_IsOriginal(bool stream, int status)
    {
        var source = Window.GetProperty("error").GetRawText(); var body = new OllamaBodyStream(Bytes(source)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, false, status: (HttpStatusCode)status))); var factory = Factory(handler, 1); await using var sdk = Build(factory, "tools", 1);
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, "tools", stream, [])); Assert.Equal((HttpStatusCode)status, failure.StatusCode); Assert.Equal(source, failure.ResponseBody); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>带竖线的调用者凭据仍原样发送，SDK 不借网关格式注入 appid。</summary>
    [Fact]
    public async Task Credential_IsOpaque()
    {
        const string key = "fixture-token|fixture-app"; using var handler = new StubHttpHandler((message, _) => { Assert.Equal(key, message.Headers.Authorization!.Parameter); Assert.False(message.Headers.Contains("appid")); return Task.FromResult(Reply(new OllamaBodyStream(Bytes(Window.GetProperty("error").GetRawText())), false, status: HttpStatusCode.Unauthorized)); });
        var factory = Factory(handler, 0); await using var sdk = Build(factory, "tools", key: key); await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, "tools")); Assert.Equal(1, handler.Calls);
    }
    /// <summary>模型后缀、思考开关和两个 token 上限不由 SDK 注入、剥离或修正。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeControls_AreNotRepaired(bool thinking)
    {
        var request = Request("reasoning"); request.Parameters["enable_thinking"] = JsonSerializer.SerializeToElement(thinking); request.Parameters["thinking"] = JsonSerializer.SerializeToElement(new { type = "enabled" }); request.Parameters["reasoning_effort"] = JsonSerializer.SerializeToElement("low"); request.Parameters["max_completion_tokens"] = JsonSerializer.SerializeToElement(256);
        var target = Target("reasoning") with { UpstreamModel = "explicit-native-model-search" };
        using var handler = new StubHttpHandler(async (message, token) => { var expected = JsonNode.Parse(Part("reasoning").GetProperty("wire").GetRawText())!.AsObject(); expected["model"] = target.UpstreamModel; expected["enable_thinking"] = thinking; expected["thinking"] = new JsonObject { ["type"] = "enabled" }; expected["reasoning_effort"] = "low"; expected["max_completion_tokens"] = 256; Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Window.GetProperty("error").GetRawText())), false, status: HttpStatusCode.BadRequest); });
        var factory = Factory(handler, 0); await using var sdk = Build(factory, "reasoning", target: target); await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, "reasoning", request: request)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生工具历史和独立 appid 客户端配置在相同完整绑定中恢复。</summary>
    [Fact]
    public async Task SameBinding_ReplaysOriginalInput()
    {
        using var handler = new StubHttpHandler(async (message, token) => { Assert.Equal("app-fixture-B", Assert.Single(message.Headers.GetValues("appid"))); Assert.True(JsonElement.DeepEquals(Part("tools").GetProperty("wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Part("tools").GetProperty("result").GetRawText())), false); }); var factory = Factory(handler, 1); var store = CompactionFixture.Store();
        for (var i = 0; i < 2; i++) { await using var sdk = Build(factory, "tools", 1, store); await Invoke(sdk, "tools"); } Assert.Equal(2, handler.Calls);
    }
    /// <summary>应用固定请求头语义改变时由调用者更新客户端名或目标版本，阻止跨应用复用。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AppContextChange_WithExplicitIdentity_IsRejected(bool changeClient)
    {
        using var handler = new StubHttpHandler((message, _) => { Assert.Equal("app-fixture-B", Assert.Single(message.Headers.GetValues("appid"))); return Task.FromResult(Reply(new OllamaBodyStream(Bytes(Part("tools").GetProperty("result").GetRawText())), false)); }); var store = CompactionFixture.Store();
        await using (var first = Build(Factory(handler, 1), "tools", 1, store)) await Invoke(first, "tools");
        var changed = new OpenAiHttpFactory(handler) { Configure = client => client.DefaultRequestHeaders.Add("appid", "app-context-changed") };
        var target = Target("tools", 1); if (!changeClient) target = target with { Version = "app-context-changed" };
        await using var second = Build(changed, "tools", 1, store, target, client: changeClient ? "Explicit.ChangedAppContext" : null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(second, "tools")); Assert.Equal(1, handler.Calls); Assert.Empty(changed.Clients);
    }
    /// <summary>完整目标、凭据和应用上下文版本漂移必须在第二次 HTTP 前失败。</summary>
    [Theory] [InlineData("root")] [InlineData("model")] [InlineData("key")] [InlineData("client")] [InlineData("policy")] [InlineData("version")]
    public async Task CompleteTargetDrift_IsRejected(string field)
    {
        const string mode = "tools"; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Part(mode).GetProperty("result").GetRawText())), false))); var factory = Factory(handler, 1); var store = CompactionFixture.Store(); await using (var first = Build(factory, mode, 1, store)) await Invoke(first, mode);
        var target = Target(mode, 1); target = field switch { "root" => target with { BaseAddress = Target(mode, 2).BaseAddress }, "model" => target with { UpstreamModel = "other" }, "version" => target with { Version = "appid-context-changed" }, _ => target };
        await using var second = Build(factory, mode, 1, store, target, key: field == "key" ? "other-key" : null, client: field == "client" ? "Explicit.ChangedAppContext" : null, options: field == "policy" ? new() { MaximumBodyBytes = 8192 } : null); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(second, mode)); Assert.Equal(1, handler.Calls);
    }
}
