using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.Azure.Tests;

/// <summary>部署、认证、API 版本与资源上限的明确目标验证。</summary>
public sealed class AzureOptionsTests
{
    /// <summary>无效固定目标不进入 HTTP 工厂。</summary>
    [Theory]
    [InlineData("id")] [InlineData("deployment")] [InlineData("dot")] [InlineData("slash")]
    [InlineData("encoded")] [InlineData("version")] [InlineData("key")] [InlineData("header")]
    [InlineData("relative")] [InlineData("scheme")] [InlineData("userinfo")] [InlineData("query")]
    [InlineData("fragment")] [InlineData("request")] [InlineData("response")]
    [InlineData("audio")] [InlineData("upload")] [InlineData("buffer")] [InlineData("body-model")]
    public void InvalidTargets(string kind)
    {
        var options = new AzureAudioOptions { Id = kind == "id" ? "" : "target", Endpoint = kind switch
            { "relative" => new Uri("relative", UriKind.Relative), "scheme" => new("wss://example.invalid/"), "userinfo" => new("https://user:secret@example.invalid/"),
                "query" => new("https://example.invalid/?key=x"), "fragment" => new("https://example.invalid/#x"), _ => new("https://example.invalid/") },
            Deployment = kind switch { "deployment" => "", "dot" => "..", "slash" => "other/deployment", "encoded" => "%2e%2e", _ => "native-deployment" },
            ApiVersion = kind == "version" ? "" : "2025-04-01-preview", ApiKey = kind == "key" ? "" : kind == "header" ? "token\r\nheader" : "token",
            BodyModel = kind == "body-model" ? "" : null, MaxRequestBytes = kind == "request" ? 0 : 16384, MaxResponseBytes = kind == "response" ? 0 : 16384,
            MaxAudioBytes = kind == "audio" ? 0 : 65536, MaxUploadBytes = kind == "upload" ? 0 : 65536, ReadBufferSize = kind == "buffer" ? 0 : 8192 };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new AzureTestFactory(handler);
        Assert.ThrowsAny<ArgumentException>(() => new AzureSpeechAdapter(factory, options));
        Assert.ThrowsAny<ArgumentException>(() => new AzureAudioFileAdapter(factory, options)); Assert.Equal(0, factory.Calls);
    }
    /// <summary>显式 API 版本始终是单个查询值，自定义路径、端口及部署点号不被归一化。</summary>
    [Fact]
    public async Task QueryValueCannotInjectParameters()
    {
        using var handler = new StubHttpHandler((message, _) =>
        {
            Assert.Equal("https://example.invalid:8443/prefix/openai/deployments/native.v1/audio/speech?api-version=2025-04-01-preview%26other%3Dvalue", message.RequestUri!.AbsoluteUri);
            return Task.FromResult(AzureFixture.Response(AzureFixture.Audio, "audio/pcm"));
        });
        var adapter = new AzureSpeechAdapter(new StubHttpClientFactory(handler), new AzureAudioOptions
        { Endpoint = new("https://example.invalid:8443/prefix/"), Deployment = "native.v1", ApiVersion = "2025-04-01-preview&other=value", ApiKey = "independent" });
        Assert.True((await AzureFixture.Collect(adapter.StreamNativeAsync(AzureFixture.Speech(), TestContext.Current.CancellationToken)))[^1].IsCompleted);
    }
}
