using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>显式根路径、版本和部署名属于供应商配置，不能靠主机或模型名称猜测。</summary>
public sealed class ConfigurationTests
{
    /// <summary>认证支持必须显式声明，外部认证没有来源时不能注册。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnsupportedCredential_IsRejectedBeforeDispatch(bool anonymous)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var provider = new AzureLlmProvider(new AzureHttpFactory(handler));
        Assert.False(provider.SupportsCredential((LlmCredentialKind)99)); Assert.False(provider.SupportsCredential(LlmCredentialKind.Anonymous));
        if (anonymous)
        {
            var context = new LlmAdapterContext(AzureFixture.Target(false, AzureLlmApiStyle.V1), LlmCredential.Anonymous("identity"), "public", "session");
            await Assert.ThrowsAsync<ArgumentException>(() => provider.CompleteAsync(AzureFixture.Request(false), context, TestContext.Current.CancellationToken));
        }
        else Assert.Throws<ArgumentException>(() => AzureFixture.Client(provider, false, external: true));
        Assert.Equal(0, handler.Calls);
    }
    /// <summary>非法版本、样式和字节上限在建立供应商时拒绝。</summary>
    [Theory] [InlineData("style")] [InlineData("missing")] [InlineData("empty")] [InlineData("space")] [InlineData("zero")] [InlineData("negative")]
    public void InvalidOptions_AreRejected(string failure)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        var options = failure switch { "style" => new AzureLlmOptions { ApiStyle = (AzureLlmApiStyle)7 }, "missing" => new() { ApiStyle = AzureLlmApiStyle.Dated }, "empty" => new() { ApiVersion = "" }, "space" => new() { ApiVersion = " " }, "zero" => new() { MaximumBodyBytes = 0 }, _ => new() { MaximumBodyBytes = -1 } };
        Assert.ThrowsAny<ArgumentException>(() => new AzureLlmProvider(new AzureHttpFactory(handler), options)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>认证来源身份同样必须稳定且可编码。</summary>
    [Theory] [InlineData("")] [InlineData(" ")]
    public void InvalidSourceIdentity_IsRejected(string identity)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var source = new AzureTokenSource(new()) { Version = identity };
        Assert.ThrowsAny<ArgumentException>(() => new AzureLlmProvider(new AzureHttpFactory(handler), tokenSource: source)); Assert.Equal(0, source.Calls);
    }
    /// <summary>直接构造孤立代理项，避免特性元数据先将测试字符串修复。</summary>
    [Fact]
    public void MalformedSourceIdentity_IsRejected()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var source = new AzureTokenSource(new()) { Version = new string((char)0xd800, 1) };
        Assert.Throws<EncoderFallbackException>(() => new AzureLlmProvider(new AzureHttpFactory(handler), tokenSource: source)); Assert.Equal(0, source.Calls);
    }
    /// <summary>令牌不能携带头分隔字符，错误格式不包含原始材料。</summary>
    [Theory] [InlineData("private value")] [InlineData("private\r\nvalue")] [InlineData("private\u0000value")]
    public void InvalidTokenText_IsRejectedWithoutEcho(string value)
    {
        var error = Assert.Throws<ArgumentException>(() => new AzureLlmAccessToken("principal", value, new AzureClock().Now)); Assert.DoesNotContain("private", error.Message);
    }
    /// <summary>版本、客户端名、上限和目标身份变更使旧 Session 失效。</summary>
    [Theory] [InlineData("version")] [InlineData("style")] [InlineData("limit")] [InlineData("client")] [InlineData("server")] [InlineData("model")] [InlineData("target-version")]
    public async Task ConfigurationChange_DoesNotReuseSession(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(AzureFixture.Reply(false))); var factory = new AzureHttpFactory(handler); var store = AzureFixture.WebSocketStore(); var target = AzureFixture.Target(false, AzureLlmApiStyle.V1);
        await using (var first = AzureFixture.Client(new(factory), false, store: store)) await AzureFixture.Invoke(first, false);
        var options = change switch { "version" => new AzureLlmOptions { ApiVersion = "preview" }, "style" => new() { ApiStyle = AzureLlmApiStyle.Dated, ApiVersion = "dated" }, "limit" => new() { MaximumBodyBytes = 1024 }, _ => new() };
        target = change switch { "server" => target with { BaseAddress = new("https://other.invalid/openai/v1/") }, "model" => target with { UpstreamModel = "other" }, "target-version" => target with { Version = "2" }, _ => target };
        var provider = new AzureLlmProvider(factory, options, httpClientName: change == "client" ? "different" : "Asgard.Loki.LLM.Azure");
        await using var next = AzureFixture.Client(provider, false, store: store, targets: [target]); await Assert.ThrowsAsync<InvalidOperationException>(() => AzureFixture.Invoke(next, false)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原样保留部署名中的点和代理前缀，只转义单个路径段和查询值。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExplicitUri_PreservesDeploymentAndEscapesVersion(bool v1)
    {
        using var handler = new StubHttpHandler((request, _) =>
        {
            Assert.Equal("https://primary.invalid/proxy/openai/" + (v1 ? "v1/chat/completions" : "deployments/deploy.1%2Fchild/chat/completions") + "?api-version=preview%26other%3Dtrue", request.RequestUri!.AbsoluteUri); return Task.FromResult(AzureFixture.Reply(false));
        });
        var style = v1 ? AzureLlmApiStyle.V1 : AzureLlmApiStyle.Dated; var target = AzureFixture.Target(false, style) with { UpstreamModel = "deploy.1/child" };
        await new AzureLlmProvider(new AzureHttpFactory(handler), new() { ApiStyle = style, ApiVersion = "preview&other=true" }).CompleteAsync(AzureFixture.Request(false), new(target, AzureFixture.Credential(false), "public", "session"), TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>无效 API 根或相对路径部署必须在认证源之前失败。</summary>
    [Theory] [InlineData("relative")] [InlineData("ftp://primary.invalid/")] [InlineData("https://user:password@primary.invalid/")] [InlineData("https://primary.invalid/?x=1")] [InlineData("https://primary.invalid/#f")] [InlineData(".")] [InlineData("..")]
    public async Task InvalidAddressOrDeployment_DoesNotAcquireToken(string input)
    {
        var source = new AzureTokenSource(new()); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new AzureHttpFactory(handler);
        var target = AzureFixture.Target(false, AzureLlmApiStyle.Dated); target = input is "." or ".." ? target with { UpstreamModel = input } : target with { BaseAddress = new(input, UriKind.RelativeOrAbsolute) };
        var provider = new AzureLlmProvider(factory, new() { ApiStyle = AzureLlmApiStyle.Dated, ApiVersion = "version" }, source);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => provider.CompleteAsync(AzureFixture.Request(false), new(target, AzureFixture.Credential(true), "public", "session"), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
    /// <summary>原生字符串在直接调用中也不能被序列化成替代字符。</summary>
    [Fact]
    public async Task InvalidInputUnicode_IsRejectedBeforeAuthentication()
    {
        var source = new AzureTokenSource(new()); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new AzureHttpFactory(handler); var input = AzureFixture.Request(false); input["future"] = "\ud800";
        await Assert.ThrowsAsync<EncoderFallbackException>(() => new AzureLlmProvider(factory, tokenSource: source).CompleteAsync(input, AzureFixture.Context(false, external: true), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
}
