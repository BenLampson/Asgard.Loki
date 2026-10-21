using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Anthropic.Tests.AnthropicHttpFixture;
using static Asgard.Loki.LLM.Anthropic.Tests.ConfiguredMessagesFixture;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>完整配置和目标身份的验证不能晚于实际 IO 或静默迁移旧会话。</summary>
public sealed class ConfiguredMessagesPolicyTests
{
    /// <summary>路径/查询、认证、实际服务器/模型/凭据、版本和本地策略均参与完整绑定。</summary>
    [Theory] [InlineData("path")] [InlineData("query")] [InlineData("auth")] [InlineData("name")] [InlineData("prefix")] [InlineData("suffix")] [InlineData("client")] [InlineData("version")] [InlineData("model")] [InlineData("root")] [InlineData("key")] [InlineData("apiVersion")] [InlineData("policy")]
    public async Task CompleteBindingDrift_IsRejectedBeforeSecondIo(string field)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Window.GetProperty("result").GetRawText())), false))); var factory = new AnthropicHttpFactory(handler); var store = Store(); var options = Options(3); await using (var first = Build(Provider(factory, 3), 3, store)) await Invoke(first);
        var transport = options.Transport!; options = field switch { "path" => options with { Transport = transport with { MessagesPath = "other.run" } }, "query" => options with { Transport = transport with { MessagesPath = transport.MessagesPath+"&changed=1" } }, "auth" => options with { Authentication = AnthropicAuthentication.Query }, "name" => options with { Transport = transport with { CredentialName = "x-other-key" } }, "prefix" => options with { Transport = transport with { CredentialPrefix = "Other " } }, "suffix" => options with { Transport = transport with { CredentialSuffix = "/changed" } }, "policy" => options with { MaximumBodyBytes = 8192 }, _ => options };
        var provider = Provider(factory, 3, options, field == "client" ? "Changed.Client" : "Explicit.Messages", field == "apiVersion" ? "2024-01-01" : "2023-06-01"); var target = ConfiguredMessagesFixture.Target(provider); target = field switch { "version" => target with { Version = "changed" }, "model" => target with { UpstreamModel = "changed" }, "root" => target with { BaseAddress = new("https://other.invalid/") }, _ => target };
        await using var changed = Build(provider, 3, store, target, field == "key" ? new("key", "changed-key") : null); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(changed)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>非法路径、百分号/Unicode、字段名或认证冲突不能注册为可调用供应商。</summary>
    public static IEnumerable<object[]> InvalidConfiguration()
    {
        foreach (var path in new[] { "", " ", "//other.invalid/x", "https://other.invalid/x", "x#fragment", "x\\y", "x/../y", "x/%2e%2e/y", "x?value=%FF", "x?value=%A", "x?value=%GG", "x%0A", "{model}/run", "/", "x?value=literal space", "x?value=\ud800" }) yield return [Options(0) with { Transport = new() { MessagesPath = path } }];
        foreach (var name in new[] { "", "bad name", "Host", "Content-Type", "Accept", "Connection", "Transfer-Encoding", "anthropic-version", "x-key\r\nnext" }) yield return [Options(3) with { Transport = Options(3).Transport! with { CredentialName = name } }];
        foreach (var query in new[] { "api_key=x", "%61pi_key=x", "API_KEY=x", "api_key=x&api_key=y" }) yield return [Options(4) with { Transport = Options(4).Transport! with { MessagesPath = "run?"+query } }];
        yield return [Options(0) with { Authentication = (AnthropicAuthentication)99 }]; yield return [Options(0) with { Transport = new() { CredentialName = "x-key" } }]; yield return [Options(2) with { Transport = new() { CredentialPrefix = "prefix" } }]; yield return [Options(3) with { Transport = null }]; yield return [Options(4) with { Transport = null }]; yield return [Options(3) with { Transport = Options(3).Transport! with { CredentialSuffix = "bad\n" } }];
    }
    /// <summary>构造失败发生在工厂前，异常不回显实际调用密钥。</summary>
    [Theory] [MemberData(nameof(InvalidConfiguration))]
    public void InvalidConfiguration_FailsBeforeFactory(AnthropicHttpOptions options)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); var factory = new AnthropicHttpFactory(handler); Assert.IsAssignableFrom<ArgumentException>(Record.Exception(() => Provider(factory, 0, options))); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>默认 ApiKey/Bearer 策略序列化不增加空 Transport 字段。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void NullTransport_PreservesExistingBinding(bool bearer)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); var factory = new AnthropicHttpFactory(handler); var authentication = bearer ? AnthropicAuthentication.Bearer : AnthropicAuthentication.ApiKey; var options = new AnthropicHttpOptions { Authentication = authentication }; var first = new AnthropicLlmProvider(factory, options); var second = new AnthropicLlmProvider(factory, options with { Transport = null }); Assert.Equal(first.BindingIdentity, second.BindingIdentity); Assert.DoesNotContain("Transport", JsonSerializer.Serialize(options)); Assert.NotEqual(first.BindingIdentity, new AnthropicLlmProvider(factory, options with { Transport = new() }).BindingIdentity); Assert.False(first.SupportsCredential(LlmCredentialKind.Anonymous));
    }
    /// <summary>新增 None 不依赖空 Key 或必须提供的路径对象，默认操作仍是 messages。</summary>
    [Fact]
    public async Task ExplicitNoneWithoutPath_UsesNativeOperation()
    {
        using var handler = new StubHttpHandler((message, _) => { Assert.Equal(Window.GetProperty("root").GetString()+"messages", message.RequestUri!.OriginalString); Assert.False(message.Headers.Contains("x-api-key")); Assert.False(message.Headers.Contains("Authorization")); return Task.FromResult(Reply(new OllamaBodyStream(Bytes(Window.GetProperty("result").GetRawText())), false)); }); var factory = new AnthropicHttpFactory(handler); await using var sdk = Build(Provider(factory, 2, new() { Authentication = AnthropicAuthentication.None }), 2); await Invoke(sdk); Assert.Equal(1, handler.Calls);
    }
    /// <summary>认证种类不匹配时核心拒绝注册，不能把外部账户或缺少 Key 当作降级信号。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void CredentialKindMismatch_IsRejected(int profile)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); var factory = new AnthropicHttpFactory(handler); var provider = Provider(factory, profile); var credential = profile == 2 ? new LlmCredential("key", "unused-key") : LlmCredential.Anonymous("key"); Assert.Throws<ArgumentException>(() => Build(provider, profile, credential: credential)); Assert.Throws<ArgumentException>(() => Build(provider, profile, credential: LlmCredential.External("key", "principal"))); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
}
