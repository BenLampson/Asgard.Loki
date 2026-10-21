using System.Text;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>Express 资源策略、编码和版本根由调用者显式设置，不从 Key 或主机猜测。</summary>
public sealed class ExpressConfigurationTests
{
    /// <summary>所有本地资源上限必须为正，不能通过禁用边界来截断或丢弃原生字段。</summary>
    [Theory] [InlineData("body")] [InlineData("tail")] [InlineData("candidates")]
    public void InvalidLimits_AreRejected(string kind)
    {
        var options = kind switch { "body" => new GoogleVertexExpressLlmOptions { MaximumBodyBytes = 0 }, "tail" => new() { MaximumBufferedTerminalEvents = 0 }, _ => new() { MaximumTrackedCandidates = 0 } }; using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); Assert.Throws<ArgumentOutOfRangeException>(() => new GoogleVertexExpressLlmProvider(new VertexHttpFactory(handler), options));
    }
    /// <summary>根路径不能夹带凭据/查询；应由所选请求头承载 Key。</summary>
    [Theory] [InlineData("https://user:secret@first.invalid/v1/")] [InlineData("https://first.invalid/v1/?key=secret")]
    public async Task InvalidRoot_FailsBeforeClient(string root)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ExpressFixture.Reply())); var factory = new VertexHttpFactory(handler); var target = ExpressFixture.Target() with { BaseAddress = new(root) }; await Assert.ThrowsAsync<ArgumentException>(() => new GoogleVertexExpressLlmProvider(factory).GenerateAsync(ExpressFixture.Request(), new(target, new("identity", ExpressFixture.Key), "public", "session"), TestContext.Current.CancellationToken)); Assert.Empty(factory.Clients);
    }
    /// <summary>原始 DOM 的非法字符在工厂创建之前失败，不被序列化成替代字符。</summary>
    [Fact]
    public async Task InvalidUnicode_FailsBeforeFactory()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ExpressFixture.Reply())); var factory = new VertexHttpFactory(handler); var input = ExpressFixture.Request(); input["future"] = new string((char)0xd800, 1); await Assert.ThrowsAsync<EncoderFallbackException>(() => new GoogleVertexExpressLlmProvider(factory).GenerateAsync(input, ExpressFixture.Context(), TestContext.Current.CancellationToken)); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
}
