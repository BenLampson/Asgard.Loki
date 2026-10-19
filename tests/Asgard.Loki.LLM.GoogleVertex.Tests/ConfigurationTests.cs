using System.Text;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>Vertex 项目/区域/资源类型由调用者显式提供，配置错误不得等到取得令牌后才失败。</summary>
public sealed class ConfigurationTests
{
    /// <summary>标准 Vertex 凭据不能由匿名或普通 API Key 隐式冒充。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnsupportedAuthentication_IsRejectedEagerly(bool anonymous)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(VertexFixture.Reply())); var provider = new GoogleVertexLlmProvider(new VertexHttpFactory(handler), source, timeProvider: clock); var credential = anonymous ? LlmCredential.Anonymous("identity") : new("identity", "fixture-key");
        Assert.False(provider.SupportsCredential((LlmCredentialKind)999)); Assert.Throws<ArgumentException>(() => VertexFixture.Client(provider, credential: credential));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GenerateAsync(VertexFixture.Request(), new(VertexFixture.Target(), credential, "public", "session"), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Equal(0, handler.Calls);
    }
    /// <summary>完整标准资源必须包含项目/区域；不把短模型名或 express 根路径猜成标准资源。</summary>
    [Theory] [InlineData("configured-model")] [InlineData("publishers/google/models/model")] [InlineData("projects/p/locations/l/models/m")] [InlineData("projects/p/locations//publishers/google/models/m")] [InlineData("projects/p/locations/l/publishers/google/models/..")] [InlineData("projects/p/locations/l/endpoints/.")] [InlineData("projects/p/locations/l/endpoints/e/extra")]
    public async Task InvalidResource_FailsBeforeAuthentication(string resource)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(VertexFixture.Reply())); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexLlmProvider(factory, source, timeProvider: clock);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GenerateAsync(VertexFixture.Request(), new(VertexFixture.Target(resource: resource), LlmCredential.External("identity", "principal-a"), "public", "session"), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
    /// <summary>根地址不能隐藏认证、查询或片段，也不能是相对路径或其他协议。</summary>
    [Theory] [InlineData("relative")] [InlineData("ftp://host.invalid/")] [InlineData("https://user:secret@host.invalid/v1/")] [InlineData("https://host.invalid/v1/?key=secret")] [InlineData("https://host.invalid/v1/#fragment")]
    public async Task InvalidRoot_FailsBeforeAuthentication(string root)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(VertexFixture.Reply())); var factory = new VertexHttpFactory(handler); var target = VertexFixture.Target() with { BaseAddress = new(root, UriKind.RelativeOrAbsolute) };
        await Assert.ThrowsAsync<ArgumentException>(() => new GoogleVertexLlmProvider(factory, source, timeProvider: clock).GenerateAsync(VertexFixture.Request(), new(target, LlmCredential.External("identity", "principal-a"), "public", "session"), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
    /// <summary>每段分别转义，资源中的查询/片段字符不能变成 URL 控制信息。</summary>
    [Fact]
    public async Task ResourceComponents_AreEscapedWithoutQueryInjection()
    {
        const string resource = "projects/project?x=1/locations/global/publishers/google/models/model#revision"; var clock = new VertexClock();
        using var handler = new StubHttpHandler((request, _) => { Assert.Equal("https://first.invalid/proxy/v1/projects/project%3Fx%3D1/locations/global/publishers/google/models/model%23revision:generateContent", request.RequestUri!.AbsoluteUri); Assert.Equal("", request.RequestUri.Query); Assert.Equal("", request.RequestUri.Fragment); return Task.FromResult(VertexFixture.Reply()); });
        await new GoogleVertexLlmProvider(new VertexHttpFactory(handler), new VertexTokenSource(clock), timeProvider: clock).GenerateAsync(VertexFixture.Request(), new(VertexFixture.Target(resource: resource), LlmCredential.External("identity", "principal-a"), "public", "session"), TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>无效资源字节策略和 quota project 头在构造时失败。</summary>
    [Theory] [InlineData("zero")] [InlineData("negative")] [InlineData("")] [InlineData(" ")] [InlineData("project\r\ninjected")]
    public void InvalidOptions_AreRejected(string value)
    {
        var options = value is "zero" or "negative" ? new GoogleVertexLlmOptions { MaximumBodyBytes = value == "zero" ? 0 : -1 } : new() { QuotaProjectId = value };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); Assert.ThrowsAny<ArgumentException>(() => new GoogleVertexLlmProvider(new VertexHttpFactory(handler), new VertexTokenSource(new()), options));
    }
    /// <summary>无效原生 Unicode 不被修复，输入和源身份均在认证前检查。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MalformedUnicode_IsRejectedBeforeSource(bool sourceIdentity)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(VertexFixture.Reply())); var factory = new VertexHttpFactory(handler); var malformed = new string((char)0xd800, 1);
        if (sourceIdentity) { source.Version = malformed; Assert.Throws<EncoderFallbackException>(() => new GoogleVertexLlmProvider(factory, source)); }
        else { var input = VertexFixture.Request(); input["future"] = malformed; await Assert.ThrowsAsync<EncoderFallbackException>(() => new GoogleVertexLlmProvider(factory, source, timeProvider: clock).GenerateAsync(input, VertexFixture.Context(), TestContext.Current.CancellationToken)); }
        Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
    /// <summary>令牌不能引入头分隔符，错误消息不得回显令牌内容。</summary>
    [Theory] [InlineData("secret value")] [InlineData("secret\r\nvalue")] [InlineData("secret\u0000value")]
    public void InvalidTokenText_IsRejectedWithoutEcho(string value)
    {
        var failure = Assert.Throws<ArgumentException>(() => new GoogleVertexLlmAccessToken("principal", value, new VertexClock().Now)); Assert.DoesNotContain("secret", failure.Message);
    }
}
