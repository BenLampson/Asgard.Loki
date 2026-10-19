using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>编辑完整目标必须显式提供合法固定身份和根地址，不读取环境或其他能力配置。</summary>
public sealed class ImageEditConfigurationTests
{
    /// <summary>配置错误在构造时失败，无 HTTP 客户端或网络调用。</summary>
    [Theory] [InlineData("id")] [InlineData("model")] [InlineData("key")] [InlineData("name")] [InlineData("uri-null")]
    [InlineData("uri-relative")] [InlineData("uri-scheme")] [InlineData("uri-user")] [InlineData("uri-query")] [InlineData("uri-fragment")]
    public void InvalidTarget_IsRejectedBeforeAnyCall(string kind)
    {
        var uri = kind switch { "uri-null" => null!, "uri-relative" => new Uri("v1/", UriKind.Relative), "uri-scheme" => new("file:///tmp/"),
            "uri-user" => new("https://user:pass@images.invalid/v1/"), "uri-query" => new("https://images.invalid/v1/?x=1"),
            "uri-fragment" => new("https://images.invalid/v1/#x"), _ => EditFixture.Options.BaseAddress };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new EditTestFactory(handler);
        Assert.ThrowsAny<ArgumentException>(() => new OpenAiImageEditAdapter(factory, new()
        { Id = kind == "id" ? "" : "edit", Model = kind == "model" ? "" : "native", ApiKey = kind == "key" ? "" : "fixture-secret",
            HttpClientName = kind == "name" ? "" : "images", BaseAddress = uri }));
        Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>空构造参数和不能无损 UTF-8 编码的实际模型不能进入 HTTP。</summary>
    [Fact]
    public void NullDependenciesAndInvalidModelEncoding_AreRejected()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new EditTestFactory(handler);
        Assert.Throws<ArgumentNullException>(() => new OpenAiImageEditAdapter(null!, EditFixture.Options));
        Assert.Throws<ArgumentNullException>(() => new OpenAiImageEditAdapter(factory, null!));
        Assert.Throws<System.Text.EncoderFallbackException>(() => new OpenAiImageEditAdapter(factory, new() { Model = "\ud800", ApiKey = "fixture-secret" }));
        Assert.Empty(factory.Clients);
    }
}
