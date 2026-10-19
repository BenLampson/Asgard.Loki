using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>本地仅校验确定的协议容器与传输边界，不按模型名改写参数或查询目录。</summary>
public sealed class SpeechValidationTests
{
    /// <summary>错误的已知字段类型不能造成静默丢弃或派发。</summary>
    [Theory]
    [InlineData("input", "null")] [InlineData("input", "{}")] [InlineData("input", "1")]
    [InlineData("voice", "null")] [InlineData("voice", "[]")] [InlineData("voice", "1")]
    [InlineData("voice", "\" \"")] [InlineData("voice", "{}")] [InlineData("voice", "{\"id\":null}")]
    [InlineData("voice", "{\"id\":3}")] [InlineData("voice", "{\"id\":\"\"}")]
    [InlineData("response_format", "3")] [InlineData("response_format", "\"\"")]
    [InlineData("stream_format", "true")] [InlineData("instructions", "{}")] [InlineData("speed", "\"1\"")]
    [InlineData("speed", "[]")] [InlineData("speed", "1e999")]
    public void InvalidNativeTypes_FailBeforeCreatingClient(string field, string json)
    {
        var request = SpeechFixture.Native(); request.Parameters[field] = JsonSerializer.Deserialize<JsonElement>(json);
        var factory = Factory(); var adapter = new OpenAiSpeechAdapter(factory, SpeechFixture.Options);
        Assert.Throws<ArgumentException>(() => adapter.StreamNativeAsync(request, TestContext.Current.CancellationToken)); Assert.Empty(factory.Clients);
    }

    /// <summary>缺少必需字段、覆盖路由 model 与无效 JSON 值在派发前失败。</summary>
    [Theory] [InlineData("input")] [InlineData("voice")] [InlineData("model")] [InlineData("undefined")]
    public void InvalidNativeEnvelope_IsRejected(string kind)
    {
        var request = SpeechFixture.Native();
        if (kind is "input" or "voice") request.Parameters.Remove(kind);
        else request.Parameters[kind == "model" ? "model" : "future"] = kind == "model" ? JsonSerializer.SerializeToElement("other") : default;
        var factory = Factory(); var adapter = new OpenAiSpeechAdapter(factory, SpeechFixture.Options);
        Assert.Throws<ArgumentException>(() => adapter.StreamNativeAsync(request, TestContext.Current.CancellationToken)); Assert.Empty(factory.Clients);
    }

    /// <summary>不支持的传输模式在调用前明确拒绝，不偷偷改为二进制。</summary>
    [Theory] [InlineData("sse")] [InlineData("future")]
    public void UnsupportedStreamMode_IsExplicit(string mode)
    {
        var request = SpeechFixture.Native(); request.Parameters["stream_format"] = JsonSerializer.SerializeToElement(mode);
        var factory = Factory(); var adapter = new OpenAiSpeechAdapter(factory, SpeechFixture.Options);
        Assert.Throws<NotSupportedException>(() => adapter.StreamNativeAsync(request, TestContext.Current.CancellationToken)); Assert.Empty(factory.Clients);
    }

    /// <summary>便捷参数覆盖及不存在的采样率/嵌套映射不能静默忽略。</summary>
    [Theory] [InlineData("model")] [InlineData("input")] [InlineData("voice")] [InlineData("response_format")]
    [InlineData("sample_rate")] [InlineData("audio_parameters")]
    public void ConvenienceUnrepresentableFields_AreRejected(string field)
    {
        var request = SpeechWireTests.Convenience();
        if (field == "sample_rate") request = request with { SampleRate = 24000 };
        else if (field == "audio_parameters") request.AudioParameters["future"] = true;
        else request.Parameters[field] = "override";
        var factory = Factory(); var adapter = new OpenAiSpeechAdapter(factory, SpeechFixture.Options);
        var error = Record.Exception(() => adapter.StreamAsync(request, TestContext.Current.CancellationToken));
        if (field is "sample_rate" or "audio_parameters") Assert.IsType<NotSupportedException>(error); else Assert.IsType<ArgumentException>(error);
        Assert.Empty(factory.Clients);
    }

    /// <summary>直接调用与核心路由的参数边界一致。</summary>
    [Theory] [InlineData("native-null")] [InlineData("native-model")] [InlineData("native-parameters")]
    [InlineData("common-null")] [InlineData("common-model")] [InlineData("text")] [InlineData("voice")]
    [InlineData("format")] [InlineData("parameters")] [InlineData("audio")] [InlineData("rate")]
    public void NullOrEmptyRequest_FailsLocally(string kind)
    {
        var factory = Factory(); var adapter = new OpenAiSpeechAdapter(factory, SpeechFixture.Options);
        var native = SpeechFixture.Native(); var common = SpeechWireTests.Convenience();
        Exception? error;
        if (kind.StartsWith("native", StringComparison.Ordinal))
        {
            error = Record.Exception(() => adapter.StreamNativeAsync(kind switch
            { "native-null" => null!, "native-model" => native with { Model = " " }, _ => native with { Parameters = null! } }, TestContext.Current.CancellationToken));
        }
        else error = Record.Exception(() => adapter.StreamAsync(kind switch
        {
            "common-null" => null!, "common-model" => common with { Model = "" }, "text" => common with { Text = "" },
            "voice" => common with { Voice = "" }, "format" => common with { Format = "" }, "parameters" => common with { Parameters = null! },
            "audio" => common with { AudioParameters = null! }, _ => common with { SampleRate = 0 }
        }, TestContext.Current.CancellationToken));
        Assert.IsAssignableFrom<ArgumentException>(error); Assert.Empty(factory.Clients);
    }

    /// <summary>HTTP 根地址不能带隐式认证、查询或片段，也不允许非 HTTP 方案。</summary>
    [Theory] [InlineData("/relative", false)] [InlineData("file:///tmp/audio", true)]
    [InlineData("https://name:secret@speech.invalid/v1/", true)] [InlineData("https://speech.invalid/v1/?x=1", true)]
    [InlineData("https://speech.invalid/v1/#x", true)]
    public void InvalidEndpoint_IsRejected(string uri, bool absolute)
    {
        var options = new OpenAiSpeechOptions { Model = "tts", ApiKey = "fixture", BaseAddress = new(uri, absolute ? UriKind.Absolute : UriKind.Relative) };
        Assert.Throws<ArgumentException>(() => new OpenAiSpeechAdapter(Factory(), options));
    }

    /// <summary>必要配置及读取缓冲区有明确本地边界；借用工厂不在构造时调用。</summary>
    [Theory] [InlineData("id")] [InlineData("model")] [InlineData("key")] [InlineData("name")]
    [InlineData("uri")] [InlineData("buffer-zero")] [InlineData("buffer-large")]
    public void InvalidOptions_AreRejected(string field)
    {
        var options = new OpenAiSpeechOptions
        {
            Model = field == "model" ? " " : "tts", ApiKey = field == "key" ? "" : "fixture",
            Id = field == "id" ? "" : "speech", HttpClientName = field == "name" ? " " : "speech",
            BaseAddress = field == "uri" ? null! : new("https://speech.invalid/v1/"),
            ReadBufferSize = field switch { "buffer-zero" => 0, "buffer-large" => 1024 * 1024 + 1, _ => 3 }
        };
        var factory = Factory(); Assert.IsAssignableFrom<ArgumentException>(Record.Exception(() => new OpenAiSpeechAdapter(factory, options)));
        Assert.Empty(factory.Clients);
    }

    /// <summary>认证配置不可 JSON 序列化泄漏，构造器空对象也有明确异常。</summary>
    [Fact]
    public void SecretAndNullBoundaries_AreExplicit()
    {
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(SpeechFixture.Options));
        Assert.DoesNotContain("ApiKey", JsonSerializer.Serialize(SpeechFixture.Options));
        Assert.Throws<ArgumentNullException>(() => new OpenAiSpeechAdapter(null!, SpeechFixture.Options));
        Assert.Throws<ArgumentNullException>(() => new OpenAiSpeechAdapter(Factory(), null!));
    }

    /// <summary>工厂若被提前调用即失败，验证错误请求确实没有进入 HTTP。</summary>
    private static SpeechTestFactory Factory() => new(new StubHttpHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP")));
}
