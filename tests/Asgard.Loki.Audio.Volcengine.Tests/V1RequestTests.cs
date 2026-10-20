using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Audio.Volcengine.Tests;

/// <summary>调用前验证固定目标及原生参数，公共桥接不创建身份和请求标识。</summary>
public sealed class V1RequestTests
{
    /// <summary>缺失、别名、冲突和不支持设置在创建连接前失败。</summary>
    [Theory]
    [InlineData("appid")]
    [InlineData("cluster")]
    [InlineData("operation")]
    [InlineData("app-token")]
    [InlineData("uid")]
    [InlineData("voice")]
    [InlineData("reqid")]
    [InlineData("text")]
    [InlineData("wav")]
    [InlineData("rate")]
    [InlineData("encoding-type")]
    [InlineData("root-alias")]
    [InlineData("app-alias")]
    [InlineData("audio-alias")]
    [InlineData("request-alias")]
    [InlineData("root-model")]
    [InlineData("request-bytes")]
    [InlineData("duplicate")]
    [InlineData("unicode")]
    public void ValidateBeforeIo(string kind)
    {
        var socket = new V1Socket();
        var request = V1Fixture.Request(edit: root =>
        {
            switch (kind)
            {
                case "appid": root["app"]!["appid"] = "other"; break;
                case "cluster": root["app"]!["cluster"] = "other"; break;
                case "operation": root["request"]!["operation"] = "query"; break;
                case "app-token": root["app"]!["token"] = ""; break;
                case "uid": root["user"]!.AsObject().Remove("uid"); break;
                case "voice": root["audio"]!["voice_type"] = 1; break;
                case "reqid": root["request"]!["reqid"] = null; break;
                case "text": root["request"]!["text"] = false; break;
                case "wav": root["audio"]!["encoding"] = "wav"; break;
                case "rate": root["audio"]!["rate"] = 0; break;
                case "encoding-type": root["audio"]!["encoding"] = new JsonObject(); break;
                case "root-alias": root["App"] = new JsonObject(); break;
                case "app-alias": root["app"]!["AppId"] = "other"; break;
                case "audio-alias": root["audio"]!["Voice_Type"] = "other"; break;
                case "request-alias": root["request"]!["ReqId"] = "other"; break;
                case "root-model": root["model"] = "other"; break;
            }
        });
        if (kind is "duplicate" or "unicode")
        {
            using var document = JsonDocument.Parse(kind == "duplicate" ? "{\"x\":1,\"x\":2}" : "{\"x\":\"\\uD800\"}");
            request.Parameters["unknown"] = document.RootElement.Clone();
        }
        var adapter = new VolcengineV1SpeechAdapter(V1Fixture.Options(request: kind == "request-bytes" ? 20 : 16384), webSocketFactory: socket);
        Assert.ThrowsAny<Exception>(() => adapter.StreamNativeAsync(request, TestContext.Current.CancellationToken)); Assert.Equal(0, socket.Calls);
    }
    /// <summary>省略的固定目标及操作字段只从显式配置补入，不注入模型、身份和编码默认值。</summary>
    [Fact]
    public async Task MissingBoundFieldsAndTypedBridge()
    {
        var socket = new V1Socket("audio-final-3");
        var adapter = new VolcengineV1SpeechAdapter(V1Fixture.Options(), webSocketFactory: socket);
        var request = V1Fixture.Request(edit: root =>
        { root["app"]!.AsObject().Remove("appid"); root["app"]!.AsObject().Remove("cluster"); root["request"]!.AsObject().Remove("operation"); });
        await V1Fixture.Collect(adapter.StreamNativeAsync(request, TestContext.Current.CancellationToken));
        using var body = JsonDocument.Parse(socket.Sent[8..]);
        Assert.Equal("fixture-v1-app", body.RootElement.GetProperty("app").GetProperty("appid").GetString());
        Assert.Equal("submit", body.RootElement.GetProperty("request").GetProperty("operation").GetString());
        var root = V1Fixture.Body(); var audio = root["audio"]!.DeepClone().AsObject();
        audio.Remove("voice_type"); audio.Remove("encoding"); audio.Remove("rate"); root.Remove("audio"); root["request"]!.AsObject().Remove("text");
        var typed = new SpeechRequest { Model = "public", Text = "原生文本 😀", Voice = "voice", Format = "pcm", SampleRate = 22050, Parameters = root, AudioParameters = audio };
        var typedSocket = new V1Socket("audio-final-3");
        var stream = new VolcengineV1SpeechAdapter(V1Fixture.Options(), webSocketFactory: typedSocket).StreamAsync(typed, TestContext.Current.CancellationToken);
        root.Clear(); audio.Clear();
        await V1Fixture.Collect(stream); using var sent = JsonDocument.Parse(typedSocket.Sent[8..]);
        Assert.Equal("原生文本 😀", sent.RootElement.GetProperty("request").GetProperty("text").GetString());
        Assert.Equal("voice", sent.RootElement.GetProperty("audio").GetProperty("voice_type").GetString());
        Assert.Equal(22050, sent.RootElement.GetProperty("audio").GetProperty("rate").GetInt32());
        Assert.Equal("fixture-body-marker", sent.RootElement.GetProperty("app").GetProperty("token").GetString());
    }
    /// <summary>公共桥接不能覆盖映射字段或以无效 Unicode 触发有损修复。</summary>
    [Theory]
    [InlineData("audio")]
    [InlineData("voice_type")]
    [InlineData("encoding")]
    [InlineData("rate")]
    [InlineData("Voice_Type")]
    [InlineData("text")]
    [InlineData("Text")]
    [InlineData("unicode")]
    public void TypedConflicts(string kind)
    {
        var root = V1Fixture.Body(); root.Remove("audio"); root["request"]!.AsObject().Remove("text");
        var audio = new JsonObject();
        if (kind == "audio") root["audio"] = new JsonObject();
        else if (kind is "text" or "Text") root["request"]![kind] = "override";
        else if (kind == "unicode") root["unknown"] = "\ud800";
        else audio[kind] = 1;
        var socket = new V1Socket(); var adapter = new VolcengineV1SpeechAdapter(V1Fixture.Options(), webSocketFactory: socket);
        Assert.ThrowsAny<Exception>(() => adapter.StreamAsync(new SpeechRequest { Model = "route", Text = "text", Voice = "voice", Parameters = root, AudioParameters = audio }, TestContext.Current.CancellationToken));
        Assert.Equal(0, socket.Calls);
    }
}
