using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Audio.Volcengine.Tests;

/// <summary>独立二进制样本验证原生调用的字段、消息与终态，不连接网络。</summary>
public sealed class V1WebSocketTests
{
    /// <summary>发送完整原生请求并保留音频顺序及元数据，末包延迟到关闭和释放之后。</summary>
    [Theory]
    [InlineData(false, "audio-final-2")]
    [InlineData(false, "audio-final-3")]
    [InlineData(true, "audio-final-3")]
    public async Task NativeRoundTrip(bool compress, string final)
    {
        using var socket = new V1Socket("ack", "audio-1", "frontend-json", "audio-2", final) { FragmentSize = 3 };
        var adapter = new VolcengineV1SpeechAdapter(V1Fixture.Options(compress: compress), webSocketFactory: socket);
        var chunks = new List<SpeechChunk>();
        await foreach (var chunk in adapter.StreamNativeAsync(V1Fixture.Request(), TestContext.Current.CancellationToken))
        { if (chunk.IsCompleted) { Assert.Equal(1, socket.Closes); Assert.Equal(1, socket.Disposals); } chunks.Add(chunk); }
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, chunks.SelectMany(x => x.Audio.ToArray()).ToArray());
        Assert.Equal(5, chunks.Count); Assert.True(chunks[^1].IsCompleted); Assert.All(chunks[..^1], x => Assert.False(x.IsCompleted));
        Assert.Equal(V1Fixture.Frame("frontend-json"), Convert.FromBase64String(chunks[2].Data.GetProperty("rawFrameBase64").GetString()!));
        Assert.Equal("volcengine.v1.websocket", chunks[2].Data.GetProperty("kind").GetString());
        var native = chunks[2].Data.GetProperty("nativeData");
        var expected = V1Fixture.Root.GetProperty("server_frames").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "frontend-json");
        using var frontend = JsonDocument.Parse(Convert.FromBase64String(expected.GetProperty("payload_base64").GetString()!));
        Assert.True(JsonElement.DeepEquals(frontend.RootElement, native));
        Assert.Equal(new byte[] { 0x11, 0x10, (byte)(compress ? 0x11 : 0x10), 0 }, socket.Sent[..4]);
        Assert.Equal(socket.Sent.Length - 8, (int)BinaryPrimitives.ReadUInt32BigEndian(socket.Sent.AsSpan(4)));
        using var stream = new MemoryStream(socket.Sent[8..]);
        using var payload = compress ? new GZipStream(stream, CompressionMode.Decompress) : (Stream)stream;
        using var body = await JsonDocument.ParseAsync(payload, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(V1Fixture.Root.GetProperty("requests")[0].GetProperty("wire_request"), body.RootElement));
        Assert.Equal(1, socket.Calls);
    }

    /// <summary>服务端 gzip、无序号音频、可省略设置及末包后的元数据不丢失。</summary>
    [Theory]
    [InlineData("audio-gzip-1", "audio-gzip-final")]
    [InlineData("audio-no-sequence", "audio-final-3")]
    public async Task GzipAndTrailingMetadata(string first, string final)
    {
        using var socket = new V1Socket(first, final, "frontend-gzip");
        var chunks = await V1Fixture.Collect(new VolcengineV1SpeechAdapter(V1Fixture.Options(), webSocketFactory: socket)
            .StreamNativeAsync(V1Fixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(3, chunks.Count); Assert.False(chunks[1].IsCompleted); Assert.True(chunks[2].IsCompleted);
        Assert.Empty(chunks[2].Audio.ToArray()); Assert.NotEqual(JsonValueKind.Null, chunks[2].Data.GetProperty("nativeData").ValueKind);
        using var omitted = new V1Socket("audio-final-3");
        var sparse = await V1Fixture.Collect(new VolcengineV1SpeechAdapter(V1Fixture.Options(), webSocketFactory: omitted)
            .StreamNativeAsync(V1Fixture.Request(2), TestContext.Current.CancellationToken));
        using var sent = JsonDocument.Parse(omitted.Sent[8..]);
        Assert.False(sent.RootElement.GetProperty("audio").TryGetProperty("encoding", out _));
        Assert.False(sent.RootElement.GetProperty("request").TryGetProperty("model", out _)); Assert.True(Assert.Single(sparse).IsCompleted);
    }

    /// <summary>错误码保留 UInt32 全范围和原始字节，晚错误不发布成功。</summary>
    [Theory]
    [InlineData("error-text", 3001L, false)]
    [InlineData("error-uint32-gzip", 4294967295L, false)]
    [InlineData("error-uint32-gzip", 4294967295L, true)]
    public async Task ErrorsPreserveRaw(string error, long code, bool late)
    {
        using var socket = late ? new V1Socket("audio-1", "audio-final-3", error) : new V1Socket(error);
        var chunks = new List<SpeechChunk>();
        var failure = await Assert.ThrowsAsync<VolcengineV1SpeechException>(async () =>
        {
            await foreach (var chunk in new VolcengineV1SpeechAdapter(V1Fixture.Options(), webSocketFactory: socket)
                .StreamNativeAsync(V1Fixture.Request(), TestContext.Current.CancellationToken)) chunks.Add(chunk);
        });
        Assert.Equal(code, failure.NativeCode); Assert.Equal(V1Fixture.Frame(error), failure.RawResponse.ToArray());
        var expected = V1Fixture.Root.GetProperty("server_frames").EnumerateArray().Single(x => x.GetProperty("name").GetString() == error);
        Assert.Equal(Convert.FromBase64String(expected.GetProperty("payload_base64").GetString()!), failure.DecodedPayload.ToArray());
        Assert.All(chunks, x => Assert.False(x.IsCompleted)); Assert.Equal(1, socket.Calls); Assert.Equal(1, socket.Disposals);
    }

    /// <summary>提前关闭、额外音频、异常关闭或关闭超时都不能完成调用。</summary>
    [Theory]
    [InlineData("early")]
    [InlineData("duplicate-final")]
    [InlineData("late-audio")]
    [InlineData("abnormal")]
    [InlineData("timeout")]
    [InlineData("send")]
    [InlineData("close")]
    [InlineData("dispose")]
    [InlineData("text")]
    public async Task LifecycleFailures(string kind)
    {
        var socket = new V1Socket("audio-1") { AutoClose = kind != "timeout", FailSend = kind == "send", FailClose = kind == "close",
            FailDispose = kind == "dispose", Text = kind == "text", PeerStatus = kind == "abnormal" ? System.Net.WebSockets.WebSocketCloseStatus.InternalServerError : System.Net.WebSockets.WebSocketCloseStatus.NormalClosure };
        if (kind == "early") socket.EnqueueClose(); else socket.Enqueue(V1Fixture.Frame("audio-final-3"));
        if (kind == "duplicate-final") socket.Enqueue(V1Fixture.Frame("audio-final-2"));
        if (kind == "late-audio") socket.Enqueue(V1Fixture.Frame("audio-2"));
        var chunks = new List<SpeechChunk>();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var chunk in new VolcengineV1SpeechAdapter(V1Fixture.Options(timeoutMs: 30), webSocketFactory: socket)
                .StreamNativeAsync(V1Fixture.Request(), TestContext.Current.CancellationToken)) chunks.Add(chunk);
        });
        Assert.All(chunks, x => Assert.False(x.IsCompleted)); Assert.Equal(1, socket.Calls); Assert.Equal(1, socket.Disposals);
    }

    /// <summary>请求在连接前冻结，释放时取消也不能泄漏完成帧。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotAndCancellation(bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var request = V1Fixture.Request();
        var socket = new V1Socket("audio-final-3") { OnConnect = request.Parameters.Clear, OnDispose = () => { if (cancel) cancellation.Cancel(); } };
        var adapter = new VolcengineV1SpeechAdapter(V1Fixture.Options(), webSocketFactory: socket);
        var stream = adapter.StreamNativeAsync(request, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => V1Fixture.Collect(stream));
        else Assert.True(Assert.Single(await V1Fixture.Collect(stream)).IsCompleted);
        using var sent = JsonDocument.Parse(socket.Sent[8..]); Assert.True(sent.RootElement.TryGetProperty("request", out _));
        Assert.Equal(1, socket.Disposals);
    }

    /// <summary>只收到空 ACK 和空末包不能冒充成功音频输出。</summary>
    [Fact]
    public async Task EmptyAudioCannotComplete()
    {
        var socket = new V1Socket("ack"); var final = V1Fixture.Frame("audio-final-3")[..12];
        BinaryPrimitives.WriteUInt32BigEndian(final.AsSpan(8), 0); socket.Enqueue(final);
        await Assert.ThrowsAsync<InvalidDataException>(() => V1Fixture.Collect(new VolcengineV1SpeechAdapter(V1Fixture.Options(), webSocketFactory: socket)
            .StreamNativeAsync(V1Fixture.Request(), TestContext.Current.CancellationToken)));
        Assert.Equal(1, socket.Disposals);
    }

    /// <summary>调用者停止枚举时只释放当前连接；不会等待或伪造末包。</summary>
    [Fact]
    public async Task ConsumerStopsBeforeFinal()
    {
        var socket = new V1Socket("audio-1", "audio-final-3");
        var stream = new VolcengineV1SpeechAdapter(V1Fixture.Options(), webSocketFactory: socket)
            .StreamNativeAsync(V1Fixture.Request(), TestContext.Current.CancellationToken);
        await using (var enumerator = stream.GetAsyncEnumerator(TestContext.Current.CancellationToken))
        { Assert.True(await enumerator.MoveNextAsync()); Assert.False(enumerator.Current.IsCompleted); }
        Assert.Equal(1, socket.Disposals); Assert.Equal(0, socket.Closes); Assert.Equal(1, socket.Calls);
    }

    /// <summary>连接工厂或接收边界取消，即使替代传输忽略取消也不能发布成功。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationAtTransportBoundary(bool connect)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var socket = new V1Socket("audio-final-3") { OnConnect = connect ? cancellation.Cancel : null, OnReceive = connect ? null : cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => V1Fixture.Collect(new VolcengineV1SpeechAdapter(V1Fixture.Options(), webSocketFactory: socket)
            .StreamNativeAsync(V1Fixture.Request(), cancellation.Token)));
        Assert.Equal(1, socket.Disposals);
    }
}
