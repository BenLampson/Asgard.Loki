using System.Text;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>未选择认证、端点/模型混淆及非法编码均在借用来源之前失败。</summary>
public sealed class MaaSConfigurationTests
{
    /// <summary>MaaS 不猜测普通 Key 是云端 OAuth，不继承 Gemini/Claude 协议。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnselectedCredential_IsRejected(bool anonymous)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(MaaSFixture.Reply())); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexMaaSLlmProvider(factory, source); var credential = anonymous ? LlmCredential.Anonymous("identity") : new("identity", "fixture-key"); Assert.False(provider.SupportsCredential((LlmCredentialKind)999)); Assert.Throws<ArgumentException>(() => MaaSFixture.Client(provider, credential: credential)); await Assert.ThrowsAsync<ArgumentException>(() => provider.CompleteAsync(MaaSFixture.Request(), MaaSFixture.Context(credential: credential), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
    /// <summary>根地址显式包含项目/区域/MaaS 端点，不接受标准 Gemini 根、相对地址或隐藏认证。</summary>
    [Theory] [InlineData("relative")] [InlineData("ftp://host.invalid/v1/projects/p/locations/l/endpoints/openapi")] [InlineData("https://user:secret@host.invalid/v1/projects/p/locations/l/endpoints/openapi")] [InlineData("https://host.invalid/v1/projects/p/locations/l/endpoints/openapi?key=secret")] [InlineData("https://host.invalid/v1/projects/p/locations/l/endpoints/openapi#fragment")] [InlineData("https://host.invalid/v1/")] [InlineData("https://host.invalid/v1/projects/p/locations/l/endpoints/other")] [InlineData("https://host.invalid/v1/projects//locations/l/endpoints/openapi")] [InlineData("https://host.invalid/v1/projects/p/locations/%20/endpoints/openapi")]
    public async Task InvalidEndpoint_FailsBeforeToken(string root)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(MaaSFixture.Reply())); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexMaaSLlmProvider(factory, source); var context = MaaSFixture.Context(MaaSFixture.Target() with { BaseAddress = new(root, UriKind.RelativeOrAbsolute) }); await Assert.ThrowsAsync<ArgumentException>(() => provider.CompleteAsync(MaaSFixture.Request(), context, TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
    /// <summary>显式 publisher/model 不根据 -maas 后缀或裸模型推断供应商。</summary>
    [Theory] [InlineData("bare-maas")] [InlineData("publishers/meta/models/model")] [InlineData("/model")] [InlineData("meta/")] [InlineData("meta/..")] [InlineData("meta/ ")]
    public async Task InvalidModel_FailsBeforeToken(string model)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(MaaSFixture.Reply())); var factory = new VertexHttpFactory(handler); await Assert.ThrowsAsync<ArgumentException>(() => new GoogleVertexMaaSLlmProvider(factory, source).CompleteAsync(MaaSFixture.Request(), MaaSFixture.Context(MaaSFixture.Target() with { UpstreamModel = model }), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
    /// <summary>无效 byte/quota 配置立即拒绝，不向借用来源委托配置校验。</summary>
    [Theory] [InlineData("zero")] [InlineData("negative")] [InlineData("")] [InlineData(" ")] [InlineData("quota\r\nother")]
    public void InvalidOptions_AreRejected(string value)
    {
        var options = value switch { "zero" => new GoogleVertexMaaSLlmOptions { MaximumBodyBytes = 0 }, "negative" => new() { MaximumBodyBytes = -1 }, _ => new() { QuotaProjectId = value } }; using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); Assert.ThrowsAny<ArgumentException>(() => new GoogleVertexMaaSLlmProvider(new VertexHttpFactory(handler), new VertexTokenSource(new()), options));
    }
    /// <summary>未知原生字段中的无效 Unicode 也不能被克隆/序列化替换。</summary>
    [Fact]
    public async Task InvalidInputUnicode_FailsBeforeToken()
    {
        var source = new VertexTokenSource(new()); using var handler = new StubHttpHandler((_, _) => Task.FromResult(MaaSFixture.Reply())); var factory = new VertexHttpFactory(handler); var input = MaaSFixture.Request(); input["future"] = new string((char)0xd800, 1); await Assert.ThrowsAsync<EncoderFallbackException>(() => new GoogleVertexMaaSLlmProvider(factory, source).CompleteAsync(input, MaaSFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
}
