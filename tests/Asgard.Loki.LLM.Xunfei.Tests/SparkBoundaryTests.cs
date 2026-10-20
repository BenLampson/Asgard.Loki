using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Xunfei.Tests;

/// <summary>原生异步/关闭/释放失败与限额均不得发布成功终态，超时通过逻辑计时器推进。</summary>
public sealed class SparkBoundaryTests
{
    /// <summary>连接、发送、关闭及释放边界的错误、取消、到期和依赖漂移保持单次调用。</summary>
    [Theory] [InlineData("connect-error")] [InlineData("send-error")] [InlineData("read-error")] [InlineData("close-error")] [InlineData("dispose-error")]
    [InlineData("connect-cancel")] [InlineData("send-cancel")] [InlineData("close-cancel")] [InlineData("dispose-cancel")]
    [InlineData("connect-expired")] [InlineData("send-expired")] [InlineData("close-expired")] [InlineData("dispose-expired")]
    [InlineData("connect-source")] [InlineData("send-source")] [InlineData("close-source")] [InlineData("dispose-source")]
    [InlineData("connect-factory")] [InlineData("send-factory")] [InlineData("close-factory")] [InlineData("dispose-factory")] [InlineData("body-change")]
    public async Task FailuresNeverDeliverNativeTerminalOrRetry(string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); using var source = new SparkSource(); using var factory = new SparkSocketFactory(); var clock = new SparkClock(); var expiry = clock.Now.AddMinutes(1); source.Credentials = new("fixture-spark-principal", "fixture-key", "fixture-secret", expiry);
        void Mutate() { if (kind.EndsWith("-error", StringComparison.Ordinal)) throw new IOException("fixture operation failure"); if (kind.EndsWith("-cancel", StringComparison.Ordinal)) cancellation.Cancel(); if (kind.EndsWith("-expired", StringComparison.Ordinal)) clock.Now = expiry; if (kind.EndsWith("-source", StringComparison.Ordinal)) source.BindingIdentity = "changed-source"; if (kind.EndsWith("-factory", StringComparison.Ordinal)) factory.BindingIdentity = "changed-factory"; }
        if (kind.StartsWith("connect-", StringComparison.Ordinal)) factory.Connect = _ => { Mutate(); return Task.FromResult<WebSocket>(factory.Socket); };
        if (kind.StartsWith("send-", StringComparison.Ordinal) || kind == "body-change") factory.Socket.OnSend = (buffer, _) => { if (kind == "body-change") buffer.Array![buffer.Offset] = (byte)'!'; else Mutate(); return Task.CompletedTask; };
        if (kind == "read-error") factory.Socket.OnRead = _ => throw new IOException("fixture read failure"); if (kind.StartsWith("close-", StringComparison.Ordinal)) factory.Socket.OnClose = _ => { Mutate(); return Task.CompletedTask; }; if (kind.StartsWith("dispose-", StringComparison.Ordinal)) factory.Socket.OnDispose = Mutate;
        List<SparkEvent> events = []; Assert.NotNull(await Record.ExceptionAsync(async () => { await foreach (var item in new XunfeiSparkProvider(source, SparkFixture.Policy(), factory, timeProvider: clock).StreamAsync(SparkFixture.Body(), SparkFixture.Context(), cancellation.Token)) events.Add(item); }));
        Assert.DoesNotContain(events, SparkStateTests.Terminal); Assert.Equal(1, source.Calls); Assert.Equal(1, factory.Calls); Assert.InRange(factory.Socket.Sends, 0, 1); Assert.Equal(kind == "connect-error" ? 0 : 1, factory.Socket.Disposals); Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
    }
    /// <summary>正常对端确认与握手都有明确时间上限，不依赖真实等待或收到模型终态后永久挂起。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task HandshakeAndCloseTimeoutUseBorrowedClock(bool close)
    {
        using var source = new SparkSource(); using var factory = new SparkSocketFactory(); var clock = new SparkClock();
        if (!close) factory.Connect = async token => { clock.FireActiveTimers(); await Task.Delay(Timeout.Infinite, token); return factory.Socket; };
        else factory.Socket.OnRead = async token => { if (factory.Socket.CloseOutputs > 0) { clock.FireActiveTimers(); await Task.Delay(Timeout.Infinite, token); } };
        List<SparkEvent> events = []; await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await foreach (var item in new XunfeiSparkProvider(source, SparkFixture.Policy(), factory, timeProvider: clock).StreamAsync(SparkFixture.Body(), SparkFixture.Context(), TestContext.Current.CancellationToken)) events.Add(item); });
        Assert.DoesNotContain(events, SparkStateTests.Terminal); Assert.Equal(close ? 1 : 0, factory.Socket.Disposals); Assert.Equal(1, factory.Calls); Assert.All(clock.Timers, t => Assert.True(t.Disposed));
    }
    /// <summary>完整模式遇到晚到关闭失败也不返回已收集的部分窗口。</summary>
    [Fact]
    public async Task CompleteCallNeverReturnsWindowOnDisposeFailure()
    {
        using var source = new SparkSource(); using var factory = new SparkSocketFactory(); factory.Socket.OnDispose = () => throw new IOException("fixture disposal failure"); await Assert.ThrowsAsync<IOException>(() => new XunfeiSparkProvider(source, SparkFixture.Policy(), factory, timeProvider: new SparkClock()).CompleteAsync(SparkFixture.Body(), SparkFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Socket.Disposals); Assert.Equal(1, factory.Socket.Sends);
    }
    /// <summary>原生消息、总字节、消息数、空分片及终态窗口均有显式上限，不截断再报成功。</summary>
    [Theory] [InlineData("message")] [InlineData("total")] [InlineData("events")] [InlineData("fragments")] [InlineData("empty-fragments")] [InlineData("tail")]
    public async Task TransportBoundsNeverTruncateIntoSuccess(string kind)
    {
        using var source = new SparkSource(); using var factory = new SparkSocketFactory(); if (kind == "tail") factory.Socket = SparkSocket.Native("split_terminal_frames"); if (kind == "empty-fragments") factory.Socket.EmptyFragments = true;
        var policy = SparkFixture.Policy() with { MaximumMessageBytes = kind == "message" ? 1 : 1024 * 1024, MaximumResponseBytes = kind == "total" ? 1 : 32 * 1024 * 1024, MaximumEvents = kind == "events" ? 3 : 8192, MaximumFragmentsPerMessage = kind is "fragments" or "empty-fragments" ? 1 : 8192, MaximumBufferedTerminalEvents = kind == "tail" ? 1 : 128 }; List<SparkEvent> events = [];
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in new XunfeiSparkProvider(source, policy, factory, timeProvider: new SparkClock()).StreamAsync(SparkFixture.Body(), SparkFixture.Context(), TestContext.Current.CancellationToken)) events.Add(item); }); Assert.DoesNotContain(events, SparkStateTests.Terminal); Assert.Equal(1, factory.Socket.Disposals); Assert.Equal(1, factory.Calls);
    }
    /// <summary>原生控制字段、必传消息形状、Unicode 与实际字节上限在认证和连接之前检查。</summary>
    [Theory] [InlineData("model")] [InlineData("stream")] [InlineData("appid")] [InlineData("domain")] [InlineData("casing")] [InlineData("messages")]
    [InlineData("role")] [InlineData("content")] [InlineData("unicode")] [InlineData("duplicate")] [InlineData("bytes")] [InlineData("cancel")]
    public async Task InvalidInputDoesNotAcquireOrConnect(string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); using var source = new SparkSource(); using var factory = new SparkSocketFactory(); var body = SparkFixture.Body();
        if (kind is "model" or "stream") body[kind] = false; if (kind == "appid") body["header"]!["app_id"] = "other"; if (kind == "domain") body["parameter"]!["chat"]!["domain"] = "other"; if (kind == "casing") body["Header"] = new JsonObject(); if (kind == "messages") body["payload"]!["message"]!["text"] = new JsonArray(); if (kind == "role") body["payload"]!["message"]!["text"]![0]!["role"] = false; if (kind == "content") body["payload"]!["message"]!["text"]![0]!["content"] = null;
        if (kind == "unicode") body["future"] = new string((char)0xd800, 1); if (kind == "duplicate") body["future"] = JsonNode.Parse("{\"x\":1,\"x\":2}"); if (kind == "cancel") cancellation.Cancel();
        Assert.NotNull(await Record.ExceptionAsync(() => SparkFixture.ReadAsync(new(source, SparkFixture.Policy() with { MaximumRequestBytes = kind == "bytes" ? 1 : 1024 * 1024 }, factory, timeProvider: new SparkClock()), cancellation.Token, body))); Assert.Equal(0, source.Calls); Assert.Equal(0, factory.Calls);
    }
}
