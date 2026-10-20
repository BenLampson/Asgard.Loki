using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.MiniMax.Tests;

/// <summary>显式模型、接口和独立认证的本地配置验证。</summary>
public sealed class MiniMaxSpeechOptionsTests
{
    /// <summary>不通过错误地址或参数隐式切换目标。</summary>
    [Theory]
    [InlineData("model")]
    [InlineData("id")]
    [InlineData("key")]
    [InlineData("header")]
    [InlineData("client")]
    [InlineData("relative")]
    [InlineData("scheme")]
    [InlineData("query")]
    [InlineData("fragment")]
    [InlineData("userinfo")]
    [InlineData("request")]
    [InlineData("response")]
    [InlineData("audio")]
    public void InvalidOptions(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        var options = new MiniMaxSpeechOptions { Model = kind == "model" ? "" : "model", Id = kind == "id" ? "" : "id",
            ApiKey = kind == "key" ? "" : kind == "header" ? "token\r\ninvalid" : "token", HttpClientName = kind == "client" ? "" : "client",
            Endpoint = kind switch { "relative" => new Uri("relative", UriKind.Relative), "scheme" => new Uri("wss://example.invalid/tts"),
                "query" => new Uri("https://example.invalid/tts?key=x"), "fragment" => new Uri("https://example.invalid/tts#x"),
                "userinfo" => new Uri("https://user:secret@example.invalid/tts"), _ => new Uri("https://example.invalid/tts") },
            MaxRequestBytes = kind == "request" ? 0 : 8192, MaxResponseBytes = kind == "response" ? 0 : 8192, MaxAudioBytes = kind == "audio" ? 0 : 8192 };
        Assert.ThrowsAny<ArgumentException>(() => new MiniMaxSpeechAdapter(new StubHttpClientFactory(handler), options)); Assert.Equal(0, handler.Calls);
    }
}
