using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.Cloudflare.Tests;

/// <summary>固定目标及边界配置不能产生隐式路径跳转或认证替换。</summary>
public sealed class AudioOptionsTests
{
    /// <summary>错误账户、模型路径、地址或资源上限均在构造阶段拒绝。</summary>
    [Theory]
    [InlineData("id")] [InlineData("account")] [InlineData("account-path")]
    [InlineData("model")] [InlineData("model-dot")] [InlineData("model-encoded")]
    [InlineData("model-empty")] [InlineData("token")] [InlineData("header")]
    [InlineData("relative")] [InlineData("scheme")] [InlineData("query")]
    [InlineData("fragment")] [InlineData("userinfo")]
    [InlineData("format")] [InlineData("upload")]
    [InlineData("parameters")] [InlineData("response")]
    public void InvalidOptions(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new AudioTestFactory(handler);
        var options = new CloudflareAudioFileOptions
        {
            Id = kind == "id" ? "" : "target", AccountId = kind == "account" ? "" : kind == "account-path" ? "account/other" : "account",
            Model = kind switch { "model" => "", "model-dot" => "@cf/../other", "model-encoded" => "@cf/%2e%2e/other", "model-empty" => "@cf//other", _ => "@cf/openai/whisper" },
            ApiToken = kind == "token" ? "" : kind == "header" ? "invalid\r\nheader" : "token",
            InputFormat = kind == "format" ? (CloudflareAudioInputFormat)99 : CloudflareAudioInputFormat.RawBinary,
            BaseAddress = kind switch { "relative" => new Uri("relative", UriKind.Relative), "scheme" => new Uri("wss://example.invalid/"),
                "query" => new Uri("https://example.invalid/?key=x"), "fragment" => new Uri("https://example.invalid/#x"),
                "userinfo" => new Uri("https://user:secret@example.invalid/"), _ => new Uri("https://example.invalid/") },
            MaxUploadBytes = kind == "upload" ? 0 : 65536, MaxParameterBytes = kind == "parameters" ? 0 : 16384, MaxResponseBytes = kind == "response" ? 0 : 16384
        };
        Assert.ThrowsAny<ArgumentException>(() => new CloudflareAudioFileAdapter(factory, options)); Assert.Equal(0, factory.Calls);
    }
    /// <summary>自定义完整根路径保持原样，实际账户与模型只组成明确的原生接口。</summary>
    [Fact]
    public async Task ExplicitCustomRoot()
    {
        using var upload = new AudioUploadProbe(AudioFixture.Bytes);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://example.invalid:8443/native-root/accounts/another-account/ai/run/@cf/openai/whisper", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer different-token", message.Headers.Authorization!.ToString());
            Assert.Equal("application/octet-stream", message.Content!.Headers.ContentType!.ToString());
            using var sink = new MemoryStream(); await message.Content!.CopyToAsync(sink, token); return AudioFixture.Response();
        });
        var adapter = new CloudflareAudioFileAdapter(new StubHttpClientFactory(handler), new CloudflareAudioFileOptions
        { AccountId = "another-account", Model = "@cf/openai/whisper", ApiToken = "different-token", InputFormat = CloudflareAudioInputFormat.RawBinary, BaseAddress = new("https://example.invalid:8443/native-root") });
        var result = await adapter.TranscribeAsync(AudioFixture.Request(upload) with { File = new() { Content = upload, FileName = "metadata-only" } }, TestContext.Current.CancellationToken);
        Assert.Equal("@cf/openai/whisper", result.Metadata.UpstreamModel); Assert.Equal(1, handler.Calls); Assert.False(upload.Disposed);
    }
}
