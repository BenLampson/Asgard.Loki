using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Tests;

/// <summary>非法 Unicode 在快照/缓存/适配器之前失败，合法字符、未知字段和原生参数不变。</summary>
public sealed class UnicodeSnapshotTests
{
    public static TheoryData<bool, bool, string> Locations
    {
        get
        {
            TheoryData<bool, bool, string> data = [];
            foreach (var responses in new[] { false, true }) foreach (var stream in new[] { false, true })
                foreach (var location in new[] { "text", "key", "call-id", "result-id", "parameter-key" }) data.Add(responses, stream, location);
            return data;
        }
    }
    /// <summary>工具 ID 与未知字段字符串同样检查，不让替代字符伪造匹配身份。</summary>
    [Theory] [MemberData(nameof(Locations))]
    public async Task MalformedDom_IsRejectedBeforeSnapshotAndCache(bool responses, bool stream, string location)
    {
        var provider = new StubLlmProvider(); var store = WebSocketFixture.Store(); var reads = 0; store.BeforeRead = _ => { reads++; return Task.CompletedTask; };
        await using var client = UnicodeSnapshotFixture.Build(provider, store); var body = UnicodeSnapshotFixture.Body(responses); Dictionary<string, JsonElement> parameters = [];
        switch (location)
        {
            case "text": body[0]!["content"]![0]!["text"] = "\ud800"; break;
            case "key": body[0]!["future\ud800"] = false; break;
            case "call-id": if (responses) body[1]!["call_id"] = "\ud800"; else body[1]!["tool_calls"]![0]!["id"] = "\ud800"; break;
            case "result-id": body[2]![responses ? "call_id" : "tool_call_id"] = "\ud801"; break;
            default: parameters["future\ud800"] = JsonSerializer.SerializeToElement(false); break;
        }
        await Assert.ThrowsAsync<EncoderFallbackException>(() => UnicodeSnapshotFixture.Invoke(client, responses, stream, body, parameters));
        Assert.Equal(0, reads); Assert.Equal(0, store.GetOrCreateCalls); Assert.Empty(provider.Calls);
    }
    /// <summary>原始 JsonElement 的未知嵌套字符串和属性名在原生解码处失败，而非先经 serializer 修复。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task EscapedMalformedUnicode_IsNotCopiedIntoParameters(bool responses, bool key)
    {
        var provider = new StubLlmProvider(); var store = WebSocketFixture.Store(); var reads = 0; store.BeforeRead = _ => { reads++; return Task.CompletedTask; };
        await using var client = UnicodeSnapshotFixture.Build(provider, store);
        var value = JsonSerializer.Deserialize<JsonElement>(key ? "{\"nested\":[{\"\\ud800\":false}]}" : "{\"nested\":[\"\\ud800\"]}");
        var error = await Record.ExceptionAsync(() => UnicodeSnapshotFixture.Invoke(client, responses, false, UnicodeSnapshotFixture.Body(responses), new() { ["future"] = value }));
        Assert.NotNull(error); Assert.Equal(0, reads); Assert.Empty(provider.Calls);
    }
    /// <summary>合法原生 Unicode、组合字符、BOM/NUL/CRLF、扩展属性及数值不因检查被归一化。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task ValidUnicodeAndExtensions_AreUnchanged(bool responses, bool stream)
    {
        const string value = "\ufeffe\u0301🌏\r\n\0"; var provider = new StubLlmProvider(); await using var client = UnicodeSnapshotFixture.Build(provider, WebSocketFixture.Store());
        var body = UnicodeSnapshotFixture.Body(responses); body[0]!["content"]![0]!["text"] = value; body[0]!["future🌏"] = JsonValue.Create(new { Value = value });
        var parameters = new Dictionary<string, JsonElement> { ["future🌏"] = JsonSerializer.Deserialize<JsonElement>("{\"exact\":9007199254740993,\"false\":false,\"null\":null}") };
        await UnicodeSnapshotFixture.Invoke(client, responses, stream, body, parameters);
        var sent = Assert.Single(provider.Calls).Request; Assert.Equal(value, sent[responses ? "input" : "messages"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(value, sent[responses ? "input" : "messages"]![0]!["future🌏"]!["Value"]!.GetValue<string>());
        Assert.Equal("9007199254740993", sent["future🌏"]!["exact"]!.ToJsonString()); Assert.Null(sent["future🌏"]!["null"]); Assert.False(sent["future🌏"]!["false"]!.GetValue<bool>());
        Assert.Equal("actual", sent["model"]!.GetValue<string>()); Assert.Equal(stream, sent["stream"]!.GetValue<bool>());
    }
    /// <summary>公共模型和 Session 的原始身份也不能在编码中碰撞。</summary>
    [Theory] [InlineData(false, "model")] [InlineData(false, "session")] [InlineData(true, "model")] [InlineData(true, "session")] [InlineData(true, "previous")]
    public async Task MalformedCallIdentity_IsRejectedBeforeCache(bool responses, string location)
    {
        var provider = new StubLlmProvider(); var store = WebSocketFixture.Store(); var reads = 0; store.BeforeRead = _ => { reads++; return Task.CompletedTask; }; await using var client = UnicodeSnapshotFixture.Build(provider, store);
        await Assert.ThrowsAsync<EncoderFallbackException>(() => UnicodeSnapshotFixture.Invoke(client, responses, false, UnicodeSnapshotFixture.Body(responses), model: location == "model" ? "\ud800" : "public", session: location == "session" ? "\ud800" : "unicode", previous: location == "previous" ? "\ud800" : null));
        Assert.Equal(0, reads); Assert.Empty(provider.Calls);
    }
    /// <summary>配置字段参与指纹之前必须可无损编码，选中/未选中目标均预先验证。</summary>
    [Theory] [InlineData("route")] [InlineData("model")] [InlineData("target")] [InlineData("provider")] [InlineData("credential-ref")]
    [InlineData("version")] [InlineData("credential-id")] [InlineData("key")]
    public void MalformedConfigurationIdentity_IsRejected(string location)
    {
        var provider = new StubLlmProvider(); var options = UnicodeSnapshotFixture.Options(provider, WebSocketFixture.Store()); var route = options.Routes[0]; var target = route.Targets[0];
        target = location switch { "model" => target with { UpstreamModel = "\ud800" }, "target" => target with { Id = "\ud800" }, "provider" => target with { ProviderId = "\ud800" },
            "credential-ref" => target with { CredentialId = "\ud800" }, "version" => target with { Version = "\ud800" }, _ => target };
        options = options with { Routes = [route with { Model = location == "route" ? "\ud800" : route.Model, Targets = [target] }], Credentials = [new(location == "credential-id" ? "\ud800" : "key", location == "key" ? "\ud800" : "fixture-key")] };
        Assert.Throws<EncoderFallbackException>(() => new LokiClientBuilder().AddLLM(options)); Assert.Empty(provider.Calls);
    }
}
