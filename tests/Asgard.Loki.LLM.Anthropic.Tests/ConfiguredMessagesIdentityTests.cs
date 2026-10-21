using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Anthropic.Tests.AnthropicHttpFixture;
using static Asgard.Loki.LLM.Anthropic.Tests.ConfiguredMessagesFixture;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>显式原生路径与认证不接受命名客户端或处理器暗中改写实际调用身份。</summary>
public sealed class ConfiguredMessagesIdentityTests
{
    /// <summary>每种配置都拒绝未选择的工厂 Key 和 Cookie，不修改借用工厂默认值。</summary>
    [Theory] [InlineData(0, "x-goog-api-key")] [InlineData(1, "x-api-key")] [InlineData(2, "Cookie")] [InlineData(3, "Authorization")] [InlineData(4, "Authorization")]
    public async Task UnselectedDefaultCredential_IsRejected(int profile, string header)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); var factory = new AnthropicHttpFactory(handler) { Defaults = client => client.DefaultRequestHeaders.Add(header, "unselected") }; await using var sdk = Build(Provider(factory, profile), profile); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(sdk)); Assert.Equal("unselected", Assert.Single(Assert.Single(factory.Clients).DefaultRequestHeaders.GetValues(header))); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(0, handler.Calls);
    }
    /// <summary>JSON 和 SSE 都复查实际 URI、方法、选中 Key、API 版本及最后关闭后的 Host。</summary>
    public static IEnumerable<object[]> Mutations()
    { foreach (var stream in new[] { false, true }) foreach (var field in new[] { "url", "method", "key", "version", "late" }) yield return [stream, field]; }
    /// <summary>身份改变不能返回完整结果或泄露已缓冲结束。</summary>
    [Theory] [MemberData(nameof(Mutations))]
    public async Task ActualWireMutation_PreventsDelivery(bool stream, string field)
    {
        HttpRequestMessage? actual = null; using var handler = new StubHttpHandler((message, _) => { actual = message; if (field == "url") message.RequestUri = new("https://other.invalid/run"); else if (field == "method") message.Method = HttpMethod.Get; else if (field is "key" or "version") { var name = field == "key" ? "x-native-key" : "anthropic-version"; message.Headers.Remove(name); message.Headers.Add(name, "changed"); } return Task.FromResult(Reply(new OllamaBodyStream(Bytes(stream ? Window.GetProperty("sse").GetString()! : Window.GetProperty("result").GetRawText())), stream)); }); var factory = new AnthropicHttpFactory(handler) { OnDispose = () => { if (field == "late") actual!.Headers.Host = "changed.invalid"; } }; await using var sdk = Build(Provider(factory, 3), 3); List<ClaudeMessageEvent> seen = []; await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, stream, seen)); Assert.DoesNotContain(seen, Ending); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>查询名和特殊 Unicode 密钥只转义一次，重复业务查询和空值保持原文。</summary>
    [Fact]
    public async Task FixedQueryAndEscapedCredential_ArePreserved()
    {
        using var handler = new StubHttpHandler((message, _) => { Assert.Equal("https://configured.invalid/native/api/native.run?tag=1&tag=2&empty=&a%2Fb=k%2F%2B%3F%26%3D%25%E9%9B%AA", message.RequestUri!.OriginalString); return Task.FromResult(Reply(new OllamaBodyStream(Bytes(Window.GetProperty("result").GetRawText())), false)); }); var factory = new AnthropicHttpFactory(handler); var options = Options(4); options = options with { Transport = options.Transport! with { MessagesPath = "/native.run?tag=1&tag=2&empty=", CredentialName = "a/b", CredentialPrefix = "", CredentialSuffix = "" } }; await using var sdk = Build(Provider(factory, 4, options), 4); await Invoke(sdk); Assert.Equal(1, handler.Calls);
    }
    /// <summary>自定义 Authorization 前缀保持原始空白，不被 SDK 改成 Bearer。</summary>
    [Fact]
    public async Task ExplicitAuthorization_IsNotNormalized()
    {
        using var handler = new StubHttpHandler((message, _) => { Assert.Equal("Token  fixture-header/tail", Assert.Single(message.Headers.NonValidated["Authorization"])); return Task.FromResult(Reply(new OllamaBodyStream(Bytes(Window.GetProperty("result").GetRawText())), false)); }); var factory = new AnthropicHttpFactory(handler); var options = Options(3); await using var sdk = Build(Provider(factory, 3, options with { Transport = options.Transport! with { CredentialName = "Authorization" } }), 3); await Invoke(sdk); Assert.Equal(1, handler.Calls);
    }
}
