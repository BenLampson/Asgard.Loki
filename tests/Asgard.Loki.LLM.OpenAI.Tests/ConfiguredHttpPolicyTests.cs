using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;
using static Asgard.Loki.LLM.OpenAI.Tests.ConfiguredHttpFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>配置与完整目标绑定先于 IO；默认策略保持既有身份，显式改变不迁移旧会话。</summary>
public sealed class ConfiguredHttpPolicyTests
{
    /// <summary>所有原生操作分别检查路径、固定查询、凭据格式、Key 和客户端/目标上下文。</summary>
    public static IEnumerable<object[]> DriftCases()
    { foreach (var mode in Modes) foreach (var field in new[] { "path", "query", "name", "prefix", "suffix", "auth", "root", "model", "version", "key", "client" }) yield return [mode, field]; }
    /// <summary>任意已绑定传输语义改变必须在第二次请求前失败。</summary>
    [Theory] [MemberData(nameof(DriftCases))]
    public async Task CompleteConfigurationDrift_IsRejected(string mode, string field)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Part(mode).GetProperty("result").GetRawText())), false))); var factory = new OpenAiHttpFactory(handler); var store = CompactionFixture.Store(); await using (var first = Build(factory, mode, 2, store)) await Invoke(first, mode);
        var target = Target(mode); target = field switch { "root" => target with { BaseAddress = new("https://other.invalid/api/") }, "model" => target with { UpstreamModel = "changed" }, "version" => target with { Version = "changed" }, _ => target };
        var options = Options(2); options = field switch { "path" => options with { ChatCompletionsPath = "other-operation" }, "query" => options with { ResponsesPath = options.ResponsesPath+"&changed=1" }, "name" => options with { CredentialName = "x-other-key" }, "prefix" => options with { CredentialPrefix = "Other " }, "suffix" => options with { CredentialSuffix = "/changed" }, "auth" => options with { Authentication = OpenAiHttpAuthentication.Query }, _ => options };
        await using var second = Build(factory, mode, 2, store, target, field == "key" ? "changed-key" : null, field == "client" ? "Changed.Client" : "Explicit.Configured", options); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(second, mode)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>不存在的路径、其他服务器、隐式跳转、非法转义/编码和字段名不能进入网络。</summary>
    public static IEnumerable<object[]> InvalidPaths()
    { foreach (var path in new[] { "", " ", "//other.invalid/x", "https://other.invalid/x", "x#fragment", "x\\y", "x/../y", "x/%2e%2E/y", "x?key=%FF", "x?key=%A", "x?key=%GG", "x%0a", "{model}/run", "/", "x?key=literal space", "x?key=\ud800" }) yield return [path]; }
    /// <summary>构造失败不访问 HTTP 工厂，也不把相对操作解释为其他服务器。</summary>
    [Theory] [MemberData(nameof(InvalidPaths))]
    public void InvalidEndpoint_FailsBeforeFactory(string path)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); var factory = new OpenAiHttpFactory(handler);
        var failure = Record.Exception(() => new OpenAiLlmProvider(factory, new OpenAiHttpOptions { Transport = Options() with { ChatCompletionsPath = path } })); Assert.IsAssignableFrom<ArgumentException>(failure); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>认证配置必须自洽，固定查询中即便参数转义或变更大小写也不能藏入另一份 Key。</summary>
    public static IEnumerable<object[]> InvalidAuthentication()
    {
        yield return [new OpenAiHttpTransportOptions { Authentication = (OpenAiHttpAuthentication)99 }];
        yield return [Options() with { CredentialName = "x-key" }]; yield return [Options(1) with { CredentialPrefix = "secret " }];
        foreach (var name in new[] { "", "bad name", "Host", "Content-Type", "Accept", "Connection", "Transfer-Encoding", "x-key\r\nnext" }) yield return [Options(2) with { CredentialName = name }];
        yield return [Options(2) with { CredentialName = null }]; yield return [Options(3) with { CredentialName = null }];
        yield return [Options(2) with { CredentialPrefix = "bad\n" }]; yield return [Options(3) with { CredentialSuffix = "bad\ud800" }];
        foreach (var query in new[] { "api_key=x", "%61pi_key=x", "API_KEY=x", "api_key=x&api_key=y" }) yield return [Options(3) with { ChatCompletionsPath = "run?"+query }];
    }
    /// <summary>配置异常不包含调用级密钥，也不会借用任何默认 Key。</summary>
    [Theory] [MemberData(nameof(InvalidAuthentication))]
    public void InvalidAuth_FailsBeforeFactory(OpenAiHttpTransportOptions transport)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); var factory = new OpenAiHttpFactory(handler); var failure = Record.Exception(() => new OpenAiLlmProvider(factory, new OpenAiHttpOptions { Transport = transport })); Assert.IsAssignableFrom<ArgumentException>(failure); Assert.Empty(factory.Clients);
    }
    /// <summary>无认证不会从 Key 缺失推断；外部账户不被当作 API Key。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void MismatchedCredential_IsRejectedAtRegistration(int profile)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); var factory = new OpenAiHttpFactory(handler); var credential = profile == 1 ? new LlmCredential("selected-key", "unused-key") : LlmCredential.Anonymous("selected-key"); Assert.Throws<ArgumentException>(() => Build(factory, "chat", profile, credential: credential)); Assert.Throws<ArgumentException>(() => Build(factory, "chat", profile, credential: LlmCredential.External("selected-key", "account"))); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>省略新增配置不改变原来默认或显式默认资源策略的绑定字节。</summary>
    [Fact]
    public void OmittedTransport_PreservesDefaultBinding()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); var factory = new OpenAiHttpFactory(handler); var first = new OpenAiLlmProvider(factory); var second = new OpenAiLlmProvider(factory, new OpenAiHttpOptions { Transport = null }); Assert.Equal(first.BindingIdentity, second.BindingIdentity); Assert.DoesNotContain("Transport", JsonSerializer.Serialize(new OpenAiHttpOptions())); Assert.NotEqual(first.BindingIdentity, new OpenAiLlmProvider(factory, new OpenAiHttpOptions { Transport = new() }).BindingIdentity); Assert.False(first.SupportsCredential(LlmCredentialKind.Anonymous));
    }
}
