using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Rerank.SiliconFlow.Tests;

/// <summary>完整上游目标与认证保持显式，不猜测地域、模型支持或借用其他能力凭据。</summary>
public sealed class SiliconFlowRerankOptionsTests
{
    /// <summary>无效目标在构造时失败，尚未创建客户端。</summary>
    [Theory] [InlineData("relative")] [InlineData("scheme")] [InlineData("userinfo")] [InlineData("query")] [InlineData("fragment")]
    [InlineData("null-root")] [InlineData("model")] [InlineData("key")] [InlineData("id")] [InlineData("name")]
    public void InvalidTarget_IsRejectedBeforeClientCreation(string kind)
    {
        var address = kind switch
        {
            "relative" => new Uri("v1/", UriKind.Relative), "scheme" => new Uri("ftp://silicon.invalid/v1/"), "userinfo" => new Uri("https://user:key@silicon.invalid/v1/"),
            "query" => new Uri("https://silicon.invalid/v1/?key=x"), "fragment" => new Uri("https://silicon.invalid/v1/#x"), "null-root" => null!, _ => new Uri("https://silicon.invalid/v1/")
        };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new SiliconFlowRerankTestFactory(handler);
        Assert.ThrowsAny<ArgumentException>(() => new SiliconFlowRerankAdapter(factory, new()
        { BaseAddress = address, Model = kind == "model" ? " " : "model", ApiKey = kind == "key" ? " " : "key", Id = kind == "id" ? " " : "native", HttpClientName = kind == "name" ? " " : "named" }));
        Assert.Empty(factory.Names); Assert.Equal(0, handler.Calls);
    }
    /// <summary>空工厂或配置不延迟到网络阶段失败。</summary>
    [Fact]
    public void NullFactoryOrOptions_IsRejected()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        Assert.Throws<ArgumentNullException>(() => new SiliconFlowRerankAdapter(null!, SiliconFlowRerankFixtures.Options()));
        Assert.Throws<ArgumentNullException>(() => new SiliconFlowRerankAdapter(new StubHttpClientFactory(handler), null!));
    }
    /// <summary>直接适配器也验证空请求、公共模型、参数容器及 Undefined JSON。</summary>
    [Theory] [InlineData("request")] [InlineData("model")] [InlineData("parameters")] [InlineData("undefined")]
    public async Task InvalidDirectRequest_DoesNotDispatch(string kind)
    {
        var request = kind switch
        {
            "request" => null!, "model" => new RerankRequest { Model = " " }, "parameters" => new RerankRequest { Model = "public", Parameters = null! },
            _ => new RerankRequest { Model = "public", Parameters = new() { ["query"] = default } }
        };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAnyAsync<ArgumentException>(() => SiliconFlowRerankFixtures.Adapter(handler).RerankAsync(request, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>有无根路径尾斜线都保留前缀；Key 只出现在认证头，不进入 URL、报文或选项序列化。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PrefixAndNamedClient_PreserveExplicitTarget(bool slash)
    {
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://silicon.invalid/tenant/api/v2/rerank", message.RequestUri!.AbsoluteUri); Assert.Equal("secret-fixture", message.Headers.Authorization!.Parameter);
            Assert.DoesNotContain("secret-fixture", await message.Content!.ReadAsStringAsync(token)); return SiliconFlowRerankFixtures.Json(SiliconFlowRerankFixtures.Read());
        });
        using var factory = new SiliconFlowRerankTestFactory(handler); var options = new SiliconFlowRerankOptions
        { Model = "rerank-native", ApiKey = "secret-fixture", BaseAddress = new("https://silicon.invalid/tenant/api/v2" + (slash ? "/" : "")), HttpClientName = "native-silicon" };
        Assert.DoesNotContain("secret-fixture", JsonSerializer.Serialize(options));
        await new SiliconFlowRerankAdapter(factory, options).RerankAsync(SiliconFlowRerankFixtures.Request(), TestContext.Current.CancellationToken);
        Assert.Equal("native-silicon", Assert.Single(factory.Names)); Assert.False(factory.Disposed); Assert.Equal(1, factory.ClientDisposals);
    }
}
