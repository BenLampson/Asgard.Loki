using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.MiniMax.Tests;

/// <summary>损坏原生结果、参数冲突和资源策略的离线边界验证。</summary>
public sealed class MiniMaxSpeechValidationTests
{
    /// <summary>不完整状态、错误载体或损坏未知字段都不能转为成功音频。</summary>
    [Theory]
    [InlineData("base-missing")]
    [InlineData("base-type")]
    [InlineData("code-missing")]
    [InlineData("code-type")]
    [InlineData("data-missing")]
    [InlineData("data-null")]
    [InlineData("status-missing")]
    [InlineData("status-type")]
    [InlineData("status-pending")]
    [InlineData("audio-missing")]
    [InlineData("audio-null")]
    [InlineData("audio-type")]
    [InlineData("audio-empty")]
    [InlineData("hex-odd")]
    [InlineData("hex-invalid")]
    [InlineData("hex-is-url")]
    [InlineData("url-is-hex")]
    [InlineData("url-scheme")]
    [InlineData("url-credentials")]
    [InlineData("url-space")]
    [InlineData("response-limit")]
    [InlineData("audio-limit")]
    [InlineData("duplicate")]
    [InlineData("unicode")]
    [InlineData("truncated")]
    [InlineData("utf8")]
    [InlineData("length")]
    public async Task InvalidResponses(string kind)
    {
        var root = JsonNode.Parse(SpeechFixture.Root.GetProperty("responses").GetProperty("hex").GetRawText())!.AsObject();
        switch (kind)
        {
            case "base-missing": root.Remove("base_resp"); break;
            case "base-type": root["base_resp"] = 0; break;
            case "code-missing": root["base_resp"]!.AsObject().Remove("status_code"); break;
            case "code-type": root["base_resp"]!["status_code"] = "0"; break;
            case "data-missing": root.Remove("data"); break;
            case "data-null": root["data"] = null; break;
            case "status-missing": root["data"]!.AsObject().Remove("status"); break;
            case "status-type": root["data"]!["status"] = "2"; break;
            case "status-pending": root["data"]!["status"] = 1; break;
            case "audio-missing": root["data"]!.AsObject().Remove("audio"); break;
            case "audio-null": root["data"]!["audio"] = null; break;
            case "audio-type": root["data"]!["audio"] = new JsonObject(); break;
            case "audio-empty": root["data"]!["audio"] = ""; break;
            case "hex-odd": root["data"]!["audio"] = "0ab"; break;
            case "hex-invalid": root["data"]!["audio"] = "0x01"; break;
            case "hex-is-url": root["data"]!["audio"] = "https://media.example.invalid/raw"; break;
            case "url-scheme": root["data"]!["audio"] = "file:///local"; break;
            case "url-credentials": root["data"]!["audio"] = "https://user:secret@media.example.invalid/raw"; break;
            case "url-space": root["data"]!["audio"] = "https://media.example.invalid/raw audio"; break;
        }
        var json = kind switch
        {
            "duplicate" => root.ToJsonString()[..^1] + ",\"Future\":0}",
            "unicode" => root.ToJsonString()[..^1] + ",\"Unknown\":\"\\uD800\"}",
            "truncated" => root.ToJsonString()[..^1], _ => root.ToJsonString()
        };
        using var handler = new StubHttpHandler((_, _) =>
        {
            var response = SpeechFixture.JsonResponse(json);
            if (kind == "utf8") response.Content = new SpeechContent([0xff, 0xfe]);
            if (kind == "length") response.Content.Headers.ContentLength = Encoding.UTF8.GetByteCount(json) + 10;
            return Task.FromResult(response);
        });
        var adapter = new MiniMaxSpeechAdapter(new StubHttpClientFactory(handler), SpeechFixture.Options(response: kind == "response-limit" ? 20 : 16384, audio: kind == "audio-limit" ? 1 : 8192));
        await Assert.ThrowsAnyAsync<Exception>(() => SpeechFixture.Collect(adapter.StreamNativeAsync(SpeechFixture.Request(kind.StartsWith("url-", StringComparison.Ordinal) ? 1 : 0), TestContext.Current.CancellationToken)));
        Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生参数中的错误类型、身份覆盖或明确流式请求在 I/O 前失败。</summary>
    [Theory]
    [InlineData("text-missing")]
    [InlineData("text-null")]
    [InlineData("text-type")]
    [InlineData("stream")]
    [InlineData("stream-type")]
    [InlineData("output")]
    [InlineData("output-type")]
    [InlineData("model")]
    [InlineData("model-alias")]
    [InlineData("text-alias")]
    [InlineData("voice-alias")]
    [InlineData("audio-alias")]
    [InlineData("voice-type")]
    [InlineData("audio-type")]
    [InlineData("request-limit")]
    [InlineData("duplicate")]
    [InlineData("unicode")]
    public void InvalidRequests(string kind)
    {
        var request = SpeechFixture.Request(edit: root =>
        {
            switch (kind)
            {
                case "text-missing": root.Remove("text"); break;
                case "text-null": root["text"] = null; break;
                case "text-type": root["text"] = 1; break;
                case "stream": root["stream"] = true; break;
                case "stream-type": root["stream"] = "false"; break;
                case "output": root["output_format"] = "mp3"; break;
                case "output-type": root["output_format"] = 1; break;
                case "model": root["model"] = "wrong"; break;
                case "model-alias": root["Model"] = "wrong"; break;
                case "text-alias": root["Text"] = "wrong"; break;
                case "voice-alias": root["voice_setting"]!["Voice_Id"] = "wrong"; break;
                case "audio-alias": root["audio_setting"]!["Format"] = "wrong"; break;
                case "voice-type": root["voice_setting"] = null; break;
                case "audio-type": root["audio_setting"] = false; break;
            }
        });
        if (kind is "duplicate" or "unicode")
        {
            using var document = JsonDocument.Parse(kind == "duplicate" ? "{\"x\":1,\"x\":2}" : "{\"x\":\"\\uD800\"}"); request.Parameters["Future"] = document.RootElement.Clone();
        }
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new SpeechHttpFactory(handler);
        Assert.ThrowsAny<Exception>(() => new MiniMaxSpeechAdapter(factory, SpeechFixture.Options(request: kind == "request-limit" ? 20 : 16384))
            .StreamNativeAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, factory.Calls); Assert.Equal(0, handler.Calls);
    }
    /// <summary>桥接参数不能重写公共字段，原生 URL 也不伪装成公共音频字节。</summary>
    [Theory]
    [InlineData("text")]
    [InlineData("Text")]
    [InlineData("audio_setting")]
    [InlineData("voice-id")]
    [InlineData("format")]
    [InlineData("sample_rate")]
    [InlineData("output-url")]
    [InlineData("unicode")]
    public void BridgeConflicts(string kind)
    {
        var parameters = new JsonObject(); var audio = new JsonObject();
        if (kind is "text" or "Text") parameters[kind] = "override";
        if (kind == "audio_setting") parameters[kind] = new JsonObject();
        if (kind == "voice-id") parameters["voice_setting"] = new JsonObject { ["voice_id"] = "override" };
        if (kind is "format" or "sample_rate") audio[kind] = 1;
        if (kind == "output-url") parameters["output_format"] = "url";
        if (kind == "unicode") parameters["unknown"] = "\ud800";
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        Assert.ThrowsAny<Exception>(() => new MiniMaxSpeechAdapter(new StubHttpClientFactory(handler), SpeechFixture.Options())
            .StreamAsync(new SpeechRequest { Model = "route", Text = "text", Voice = "voice", Parameters = parameters, AudioParameters = audio }, TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
    }
}
