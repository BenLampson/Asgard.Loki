using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Google.Tests.ConfiguredGeminiFixture;

namespace Asgard.Loki.LLM.Google.Tests;

/// <summary>明确 Gemini 端点与认证不能被工厂或处理器静默替换。</summary>
public sealed class ConfiguredGeminiIdentityTests
{
    /// <summary>未选中工厂凭据和 Cookie 都在 HTTP 前失败；借用默认配置保持不变。</summary>
    [Theory] [InlineData(0, "Authorization")] [InlineData(1, "x-goog-api-key")] [InlineData(2, "Cookie")] [InlineData(3, "Authorization")] [InlineData(4, "x-goog-api-key")]
    public async Task UnselectedDefaultCredential_IsRejected(int profile, string header)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); var factory = new GoogleHttpFactory(handler) { Configure = client => client.DefaultRequestHeaders.Add(header, "unselected") }; await using var sdk = Build(Provider(factory, profile), profile); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(sdk)); Assert.Equal("unselected", Assert.Single(Assert.Single(factory.Clients).DefaultRequestHeaders.GetValues(header))); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(0, handler.Calls);
    }
    /// <summary>两种调用复查实际 URI、方法、凭据和最后关闭时的 Host。</summary>
    public static IEnumerable<object[]> Mutations()
    { foreach (var stream in new[] { false, true }) foreach (var field in new[] { "url", "method", "key", "late" }) yield return [stream, field]; }
    /// <summary>实际线路身份改变不能交付完整窗口或流结束。</summary>
    [Theory] [MemberData(nameof(Mutations))]
    public async Task ActualWireMutation_PreventsDelivery(bool stream, string field)
    {
        HttpRequestMessage? actual = null; using var handler = new StubHttpHandler((message, _) => { actual = message; if (field == "url") message.RequestUri = new("https://other.invalid/run"); else if (field == "method") message.Method = HttpMethod.Get; else if (field == "key") { message.Headers.Remove("x-native-key"); message.Headers.Add("x-native-key", "changed"); } return Task.FromResult(Reply(new OllamaBodyStream(Bytes(stream ? Window.GetProperty("sse").GetString()! : Window.GetProperty("result").GetRawText())), stream)); }); var factory = new GoogleHttpFactory(handler) { OnDispose = () => { if (field == "late") actual!.Headers.Host = "changed.invalid"; } }; await using var sdk = Build(Provider(factory, 3), 3); List<GeminiContentEvent> seen = []; await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, stream, seen)); Assert.DoesNotContain(seen, Ending); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>查询名和特殊密钥只转义一次，固定业务重复项及空值保持。</summary>
    [Fact]
    public async Task QueryNameAndCredential_AreEscapedOnce()
    {
        using var handler = new StubHttpHandler((message, _) => { Assert.Equal("https://configured.invalid/native/api/run/served%2Fmodel%3F%25%E9%9B%AA?tag=1&tag=2&empty=&a%2Fb=k%2F%2B%3F%26%3D%25%E9%9B%AA", message.RequestUri!.OriginalString); return Task.FromResult(Reply(new OllamaBodyStream(Bytes(Window.GetProperty("result").GetRawText())))); }); var factory = new GoogleHttpFactory(handler); var options = Options(4); options = options with { Transport = options.Transport! with { GenerateContentPath = "/run/{model}?tag=1&tag=2&empty=", CredentialName = "a/b", CredentialPrefix = "", CredentialSuffix = "" } }; await using var sdk = Build(Provider(factory, 4, options), 4); await Invoke(sdk); Assert.Equal(1, handler.Calls);
    }
    /// <summary>自定义 Authorization 的明确前缀不被规范化或改成默认 Bearer。</summary>
    [Fact]
    public async Task ExplicitAuthorization_IsNotNormalized()
    {
        using var handler = new StubHttpHandler((message, _) => { Assert.Equal("Token  fixture-header/tail", Assert.Single(message.Headers.NonValidated["Authorization"])); return Task.FromResult(Reply(new OllamaBodyStream(Bytes(Window.GetProperty("result").GetRawText())))); }); var factory = new GoogleHttpFactory(handler); var options = Options(3); await using var sdk = Build(Provider(factory, 3, options with { Transport = options.Transport! with { CredentialName = "Authorization" } }), 3); await Invoke(sdk); Assert.Equal(1, handler.Calls);
    }
    /// <summary>模型中百分号、斜杠和点片段是原标识，不能双重解码或按路径跳转归一化。</summary>
    [Theory] [InlineData("folder/../model", "folder%2F..%2Fmodel")] [InlineData("literal%2Fmodel", "literal%252Fmodel")]
    public async Task ModelIdentifier_IsEscapedAsOneValue(string model, string escaped)
    {
        using var handler = new StubHttpHandler((message, _) => { Assert.Equal("https://configured.invalid/native/api/models/"+escaped+":generateContent", message.RequestUri!.OriginalString); return Task.FromResult(Reply(new OllamaBodyStream(Bytes(Window.GetProperty("result").GetRawText())))); }); var factory = new GoogleHttpFactory(handler); await using var sdk = Build(Provider(factory, options: new() { Transport = new() }), target: Target() with { UpstreamModel = model }); await Invoke(sdk); Assert.Equal(1, handler.Calls);
    }
}
