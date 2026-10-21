using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.Volcengine.Tests;

/// <summary>固定目标及资源配置不在调用中隐式改变传输或认证。</summary>
public sealed class V1OptionsTests
{
    /// <summary>无效地址、认证和上限在构造时拒绝。</summary>
    [Theory]
    [InlineData("token")]
    [InlineData("header")]
    [InlineData("app")]
    [InlineData("cluster")]
    [InlineData("id")]
    [InlineData("relative")]
    [InlineData("http-to-ws")]
    [InlineData("query")]
    [InlineData("fragment")]
    [InlineData("userinfo")]
    [InlineData("transport")]
    [InlineData("request-limit")]
    [InlineData("message-limit")]
    [InlineData("decoded-limit")]
    [InlineData("total-limit")]
    [InlineData("frames-limit")]
    [InlineData("timeout")]
    public void InvalidOptions(string kind)
    {
        var endpoint = kind switch { "relative" => new Uri("relative", UriKind.Relative), "http-to-ws" => new Uri("https://example.invalid/tts"),
            "query" => new Uri("wss://example.invalid/tts?key=x"), "fragment" => new Uri("wss://example.invalid/tts#x"),
            "userinfo" => new Uri("wss://user:secret@example.invalid/tts"), _ => null };
        var options = new VolcengineV1SpeechOptions { Id = kind == "id" ? "" : "v1", AppId = kind == "app" ? "" : "app",
            Cluster = kind == "cluster" ? "" : "cluster", AccessToken = kind == "token" ? "" : kind == "header" ? "a\r\nb" : "access",
            Transport = kind == "transport" ? (VolcengineV1SpeechTransport)99 : VolcengineV1SpeechTransport.WebSocket, Endpoint = endpoint,
            MaxRequestBytes = kind == "request-limit" ? 0 : 4096, MaxMessageBytes = kind == "message-limit" ? 1 : 4096,
            MaxDecodedBytes = kind == "decoded-limit" ? 0 : 4096, MaxResponseBytes = kind == "total-limit" ? 0 : 65536,
            MaxFrames = kind == "frames-limit" ? 0 : 4096, CloseTimeout = kind == "timeout" ? TimeSpan.Zero : TimeSpan.FromSeconds(5) };
        Assert.ThrowsAny<ArgumentException>(() => new VolcengineV1SpeechAdapter(options));
    }
    /// <summary>HTTP 不能缺少工厂或借用 WebSocket gzip 选项。</summary>
    [Fact]
    public void HttpRequiresItsOwnFactoryAndRequestMode()
    {
        Assert.Throws<ArgumentNullException>(() => new VolcengineV1SpeechAdapter(V1Fixture.Options(VolcengineV1SpeechTransport.Http)));
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        Assert.Throws<ArgumentException>(() => new VolcengineV1SpeechAdapter(V1Fixture.Options(VolcengineV1SpeechTransport.Http, compress: true), new StubHttpClientFactory(handler)));
        Assert.Equal(0, handler.Calls);
    }
}
