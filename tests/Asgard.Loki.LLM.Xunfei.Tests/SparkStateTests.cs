using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Xunfei.Tests;

/// <summary>通过实际供应商入口验证原生状态和字段保真，不按文本保留 index 聚合消息。</summary>
public sealed class SparkStateTests
{
    /// <summary>缺失/重复/迟到/改身份消息均不能使原生结束帧进入公开输出。</summary>
    [Theory] [InlineData("mid-fragment")] [InlineData("text-end-global-missing")] [InlineData("early-close")] [InlineData("bad-close")] [InlineData("missing-close")] [InlineData("late-data")] [InlineData("sid")] [InlineData("code-type")]
    [InlineData("header-status")] [InlineData("choices-status")] [InlineData("seq-repeat")] [InlineData("seq-back")] [InlineData("seq-string")] [InlineData("status-reset")]
    [InlineData("global-before-text")] [InlineData("duplicate-text-end")] [InlineData("text-type")] [InlineData("content-type")] [InlineData("binary")] [InlineData("utf8")] [InlineData("json")] [InlineData("duplicate-json")]
    public async Task InvalidNativeWindowNeverPublishesTerminal(string kind)
    {
        using var source = new SparkSource(); using var factory = new SparkSocketFactory(); var frames = JsonNode.Parse(SparkFixture.Part("frames").GetRawText())!.AsArray();
        if (kind == "sid") frames[2]!["header"]!["sid"] = "changed"; if (kind == "code-type") frames[2]!["header"]!["code"] = "0"; if (kind == "header-status") frames[2]!["header"]!["status"] = 3; if (kind == "choices-status") frames[2]!["payload"]!["choices"]!["status"] = 3;
        if (kind == "seq-repeat") frames[2]!["payload"]!["choices"]!["seq"] = 0; if (kind == "seq-back") frames[2]!["payload"]!["choices"]!["seq"] = -1; if (kind == "seq-string") frames[2]!["payload"]!["choices"]!["seq"] = "2"; if (kind == "status-reset") frames[2]!["payload"]!["choices"]!["status"] = 0;
        if (kind == "global-before-text") frames[2]!["header"]!["status"] = 2; if (kind == "text-type") frames[2]!["payload"]!["choices"]!["text"] = false; if (kind == "content-type") frames[2]!["payload"]!["choices"]!["text"]![0]!["content"] = false;
        if (kind == "text-end-global-missing") { frames = JsonNode.Parse(SparkFixture.Part("split_terminal_frames").GetRawText())!.AsArray(); frames.RemoveAt(frames.Count - 1); }
        if (kind == "duplicate-text-end") { frames = JsonNode.Parse(SparkFixture.Part("split_terminal_frames").GetRawText())!.AsArray(); frames.Insert(4, frames[3]!.DeepClone()); }
        if (kind == "early-close") frames.RemoveAt(frames.Count - 1); var socket = new SparkSocket(); foreach (var frame in frames) socket.Add(JsonSerializer.SerializeToElement(frame));
        if (kind == "late-data") socket.Add(SparkFixture.Part("frames")[0]); if (kind == "binary") { socket = new(); socket.Add(Encoding.UTF8.GetBytes("{}"), System.Net.WebSockets.WebSocketMessageType.Binary); }
        if (kind is "utf8" or "json" or "duplicate-json") { socket = new(); socket.Add(kind == "utf8" ? [255] : Encoding.UTF8.GetBytes(kind == "json" ? "{" : "{\"header\":{},\"header\":{}}")); }
        if (kind != "missing-close") socket.AddClose(kind == "bad-close" ? System.Net.WebSockets.WebSocketCloseStatus.InternalServerError : System.Net.WebSockets.WebSocketCloseStatus.NormalClosure); if (kind == "mid-fragment") socket.CloseAtRead = 2; factory.Socket = socket; List<SparkEvent> delivered = [];
        Assert.NotNull(await Record.ExceptionAsync(async () => { await foreach (var item in new XunfeiSparkProvider(source, SparkFixture.Policy(), factory, timeProvider: new SparkClock()).StreamAsync(SparkFixture.Body(), SparkFixture.Context(), TestContext.Current.CancellationToken)) delivered.Add(item); })); Assert.DoesNotContain(delivered, Terminal); Assert.Equal(1, socket.Disposals); Assert.Equal(1, factory.Calls); Assert.Equal(0, socket.CloseCalls);
    }
    /// <summary>原生非零错误无论在终态前后都保留完整消息，不补造 HTTP 状态。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeErrorKeepsWholeOriginalMessageAndWithholdsTerminal(bool late)
    {
        using var source = new SparkSource(); using var factory = new SparkSocketFactory(); var socket = late ? SparkSocket.Native(close: false) : new SparkSocket(); socket.Add(SparkFixture.Part("error")); socket.AddClose(); factory.Socket = socket; List<SparkEvent> events = [];
        var error = await Assert.ThrowsAsync<XunfeiSparkUpstreamException>(async () => { await foreach (var item in new XunfeiSparkProvider(source, SparkFixture.Policy(), factory, timeProvider: new SparkClock()).StreamAsync(SparkFixture.Body(), SparkFixture.Context(), TestContext.Current.CancellationToken)) events.Add(item); }); Assert.Equal(10014, error.Code); Assert.True(JsonElement.DeepEquals(SparkFixture.Part("error"), error.NativeData)); Assert.True(JsonElement.DeepEquals(SparkFixture.Part("error"), JsonSerializer.Deserialize<JsonElement>(error.Body.Span))); Assert.DoesNotContain(events, Terminal); Assert.Equal(1, socket.Disposals);
    }
    internal static bool Terminal(SparkEvent item) => item.Data.GetProperty("header").GetProperty("status").GetInt32() == 2 || item.Data.GetProperty("payload").TryGetProperty("choices", out var c) && c.GetProperty("status").GetInt32() == 2;
}
