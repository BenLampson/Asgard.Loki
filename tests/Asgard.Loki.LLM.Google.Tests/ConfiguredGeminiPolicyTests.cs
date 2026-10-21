using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Google.Tests.ConfiguredGeminiFixture;

namespace Asgard.Loki.LLM.Google.Tests;

/// <summary>模型与认证的完整显式配置先于工厂调用验证，并保持已有默认绑定。</summary>
public sealed class ConfiguredGeminiPolicyTests
{
    /// <summary>两种操作路径、查询、认证及完整目标各自改变均不能复用旧 Session。</summary>
    [Theory] [InlineData("generate")] [InlineData("stream")] [InlineData("query")] [InlineData("auth")] [InlineData("name")] [InlineData("prefix")] [InlineData("suffix")] [InlineData("root")] [InlineData("model")] [InlineData("version")] [InlineData("key")] [InlineData("client")] [InlineData("policy")]
    public async Task CompleteBindingDrift_IsRejected(string field)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Window.GetProperty("result").GetRawText()))))); var factory = new GoogleHttpFactory(handler); var store = Store(); await using (var first = Build(Provider(factory, 3), 3, store)) await Invoke(first); var options = Options(3); var transport = options.Transport!;
        options = field switch { "generate" => options with { Transport = transport with { GenerateContentPath = "changed/{model}:generate" } }, "stream" => options with { Transport = transport with { StreamGenerateContentPath = "changed/{model}:stream?alt=sse" } }, "query" => options with { Transport = transport with { GenerateContentPath = transport.GenerateContentPath+"&changed=1" } }, "auth" => options with { Authentication = GoogleHttpAuthentication.Query }, "name" => options with { Transport = transport with { CredentialName = "x-other-key" } }, "prefix" => options with { Transport = transport with { CredentialPrefix = "Other " } }, "suffix" => options with { Transport = transport with { CredentialSuffix = "/changed" } }, "policy" => options with { MaximumBodyBytes = 8192 }, _ => options };
        var target = Target(3); target = field switch { "root" => target with { BaseAddress = new("https://other.invalid/api/") }, "model" => target with { UpstreamModel = "changed" }, "version" => target with { Version = "changed" }, _ => target }; await using var changed = Build(Provider(factory, 3, options, field == "client" ? "Changed.Client" : "Explicit.Gemini"), 3, store, target, field == "key" ? new("native-key", "changed-key") : null); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(changed)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>路径位置必须明确，模型不能放到查询中或被额外模板隐藏。</summary>
    public static IEnumerable<object[]> InvalidConfiguration()
    {
        foreach (var path in new[] { "", "run", "//other.invalid/{model}", "https://other.invalid/{model}", "x/{model}#fragment", "x/{model}/../run", "x/{model}/%2e%2e/run", "x/{model}?value=%FF", "x/{model}?value=%A", "x/{model}?value=%GG", "x/{model}%0A", "x/{other}/{model}", "x/{model}/{model}", "run?model={model}", "x/{model}?other={model}", "x/{model}?value=literal space", "x/{model}?value=\ud800" }) yield return [Options() with { Transport = Options().Transport! with { GenerateContentPath = path } }];
        foreach (var name in new[] { "", "bad name", "Host", "Content-Type", "Accept", "Connection", "Transfer-Encoding", "x-key\r\nnext" }) yield return [Options(3) with { Transport = Options(3).Transport! with { CredentialName = name } }];
        foreach (var query in new[] { "api_key=x", "%61pi_key=x", "API_KEY=x", "api_key=x&api_key=y" }) yield return [Options(4) with { Transport = Options(4).Transport! with { GenerateContentPath = "run/{model}?"+query } }];
        yield return [Options() with { Authentication = (GoogleHttpAuthentication)99 }]; yield return [Options() with { Transport = new() { CredentialName = "x-key" } }]; yield return [Options(2) with { Transport = new() { CredentialPrefix = "prefix" } }]; yield return [Options(3) with { Transport = null }]; yield return [Options(4) with { Transport = null }]; yield return [Options() with { Transport = Options().Transport! with { FixedUpstreamModel = "fixed" } }]; yield return [Options() with { Transport = new() { FixedUpstreamModel = "", GenerateContentPath = "fixed", StreamGenerateContentPath = "fixed" } }];
    }
    /// <summary>构造无效配置不创建客户端，不访问上游。</summary>
    [Theory] [MemberData(nameof(InvalidConfiguration))]
    public void InvalidConfiguration_FailsBeforeFactory(GoogleHttpOptions options)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); var factory = new GoogleHttpFactory(handler); Assert.IsAssignableFrom<ArgumentException>(Record.Exception(() => Provider(factory, options: options))); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>默认认证和空 Transport 都省略，保留之前四个资源策略的绑定字节。</summary>
    [Fact]
    public void OmittedPolicies_PreserveDefaultBinding()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); var factory = new GoogleHttpFactory(handler); var first = new GoogleLlmProvider(factory); var options = new GoogleHttpOptions { Authentication = GoogleHttpAuthentication.ApiKey, Transport = null }; var oldIdentity = typeof(GoogleLlmProvider).AssemblyQualifiedName+":google-http-v2:"+JsonSerializer.Serialize(new { Client = "Asgard.Loki.LLM.Google", Options = new { MaximumRequestBytes = 32 * 1024 * 1024, MaximumBodyBytes = 32 * 1024 * 1024, MaximumBufferedTerminalEvents = 128, MaximumTrackedCandidates = 128 } }); Assert.Equal(oldIdentity, first.BindingIdentity); Assert.Equal(first.BindingIdentity, new GoogleLlmProvider(factory, options).BindingIdentity); Assert.DoesNotContain("Authentication", JsonSerializer.Serialize(options)); Assert.DoesNotContain("Transport", JsonSerializer.Serialize(options)); Assert.NotEqual(first.BindingIdentity, new GoogleLlmProvider(factory, options with { Transport = new() }).BindingIdentity); Assert.False(first.SupportsCredential(LlmCredentialKind.Anonymous));
    }
    /// <summary>固定端点声明的实际模型必须等于目标；两种调用都在工厂前失败。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FixedModelMismatch_IsRejectedBeforeFactory(bool stream)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); var factory = new GoogleHttpFactory(handler); var options = Options(); options = options with { Transport = options.Transport! with { FixedUpstreamModel = "other-model", GenerateContentPath = "fixed:generate", StreamGenerateContentPath = "fixed:stream?alt=sse" } }; await using var sdk = Build(Provider(factory, options: options)); await Assert.ThrowsAsync<ArgumentException>(() => Invoke(sdk, stream, [])); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>None 不能借用 Key，其他认证也不能借用匿名或外部账户身份。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void CredentialKindMismatch_IsRejected(int profile)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); var factory = new GoogleHttpFactory(handler); var provider = Provider(factory, profile); var credential = profile == 2 ? new LlmCredential("native-key", "unused-key") : LlmCredential.Anonymous("native-key"); Assert.Throws<ArgumentException>(() => Build(provider, profile, credential: credential)); Assert.Throws<ArgumentException>(() => Build(provider, profile, credential: LlmCredential.External("native-key", "principal"))); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>固定模型声明本身进入绑定，在重新解释同一目标模型前拒绝旧 Session。</summary>
    [Fact]
    public async Task FixedModelDeclarationChange_InvalidatesBindingBeforeDispatch()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Window.GetProperty("result").GetRawText()))))); var factory = new GoogleHttpFactory(handler); var store = Store(); var options = Options(); options = options with { Transport = options.Transport! with { FixedUpstreamModel = Window.GetProperty("model").GetString(), GenerateContentPath = "fixed:generate", StreamGenerateContentPath = "fixed:stream?alt=sse" } };
        await using (var first = Build(Provider(factory, options: options), store: store)) await Invoke(first); await using var changed = Build(Provider(factory, options: options with { Transport = options.Transport! with { FixedUpstreamModel = "changed" } }), store: store); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(changed)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>明确 None 可沿用默认模型路径，不会把缺少 Key 自动当作认证选择。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExplicitNoneWithoutTransport_UsesOriginalModelOperation(bool stream)
    {
        using var handler = new StubHttpHandler((message, _) => { Assert.Equal("https://configured.invalid/native/api/models/served%2Fmodel%3F%25%E9%9B%AA:"+(stream ? "streamGenerateContent?alt=sse" : "generateContent"), message.RequestUri!.OriginalString); Assert.False(message.Headers.Contains("x-goog-api-key")); Assert.False(message.Headers.Contains("Authorization")); return Task.FromResult(Reply(new OllamaBodyStream(Bytes(stream ? Window.GetProperty("sse").GetString()! : Window.GetProperty("result").GetRawText())), stream)); }); var factory = new GoogleHttpFactory(handler); await using var sdk = Build(Provider(factory, 2, new() { Authentication = GoogleHttpAuthentication.None }), 2); await Invoke(sdk, stream, []); Assert.Equal(1, handler.Calls);
    }
}
