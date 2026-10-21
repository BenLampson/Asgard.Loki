using Xunit;

namespace Asgard.Loki.Audio.Tests;

/// <summary>错误配置在模块注册时失败，文件操作不要求额外 TTS 或 LLM 目标。</summary>
public sealed class AudioFileConfigurationTests
{
    /// <summary>配置容器不可为空，至少一种 Audio 操作必须具有显式路由。</summary>
    [Theory] [InlineData("null")] [InlineData("empty")] [InlineData("speech")]
    [InlineData("transcriptions")] [InlineData("translations")]
    public void InvalidOptions_FailAtRegistration(string kind)
    {
        var options = kind switch
        {
            "null" => null!, "speech" => new AudioOptions { SpeechRoutes = null! },
            "transcriptions" => new AudioOptions { TranscriptionRoutes = null! }, "translations" => new AudioOptions { TranslationRoutes = null! }, _ => new AudioOptions()
        };
        Assert.IsAssignableFrom<ArgumentException>(Record.Exception(() => new LokiClientBuilder().AddAudio(options)));
    }

    /// <summary>空路由、模型、目标、目标身份及重复模型都不能延迟到上传之后才失败。</summary>
    [Theory]
    [InlineData(false, "route")] [InlineData(true, "route")] [InlineData(false, "model")] [InlineData(true, "model")]
    [InlineData(false, "targets")] [InlineData(true, "targets")] [InlineData(false, "empty")] [InlineData(true, "empty")]
    [InlineData(false, "target")] [InlineData(true, "target")] [InlineData(false, "id")] [InlineData(true, "id")]
    [InlineData(false, "duplicate")] [InlineData(true, "duplicate")]
    public void InvalidRoute_FailsAtRegistration(bool translation, string kind)
    {
        var adapter = new StubAudioFileAdapter(kind == "id" ? " " : "file-one");
        IReadOnlyList<IAudioTranscriptionAdapter> transcribe = kind switch { "targets" => null!, "empty" => [], "target" => [null!], _ => [adapter] };
        IReadOnlyList<IAudioTranslationAdapter> translate = kind switch { "targets" => null!, "empty" => [], "target" => [null!], _ => [adapter] };
        var routeOne = new AudioTranscriptionRoute { Model = kind == "model" ? " " : "file", Targets = transcribe };
        var routeTwo = new AudioTranslationRoute { Model = kind == "model" ? " " : "file", Targets = translate };
        var options = translation ? new AudioOptions { TranslationRoutes = kind switch { "route" => [null!], "duplicate" => [routeTwo, routeTwo], _ => [routeTwo] } }
            : new AudioOptions { TranscriptionRoutes = kind switch { "route" => [null!], "duplicate" => [routeOne, routeOne], _ => [routeOne] } };
        Assert.IsAssignableFrom<ArgumentException>(Record.Exception(() => new LokiClientBuilder().AddAudio(options)));
        Assert.Equal(0, adapter.Transcribes + adapter.Translates + adapter.Streams);
    }
}
