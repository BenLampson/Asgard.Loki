using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>完成事件必须与正常结束、尾部读取和资源释放一起成立，不能把 EOF 当 done。</summary>
public sealed class SpeechSseLifecycleTests
{
    /// <summary>缺少 done、重复终态或完成后数据都失败，且直接适配器也不提前暴露终态。</summary>
    [Theory]
    [InlineData(false, "empty")] [InlineData(true, "empty")] [InlineData(false, "missing")] [InlineData(true, "missing")]
    [InlineData(false, "duplicate")] [InlineData(true, "duplicate")] [InlineData(false, "late")] [InlineData(true, "late")]
    [InlineData(false, "sentinel")] [InlineData(true, "sentinel")] [InlineData(false, "truncated")] [InlineData(true, "truncated")]
    public async Task InvalidSequence_NeverPublishesSuccess(bool direct, string kind)
    {
        var prefix = SpeechSseFixture.Event(SpeechSseFixture.Delta); var done = SpeechSseFixture.Event(SpeechSseFixture.Done);
        var body = kind switch
        {
            "empty" => "", "missing" => prefix, "duplicate" => prefix + done + done,
            "late" => prefix + done + SpeechSseFixture.Event("{\"type\":\"future\"}"),
            "sentinel" => prefix + SpeechSseFixture.Event("[DONE]"), _ => prefix + "data: " + SpeechSseFixture.Done + "\n"
        };
        var stream = SpeechSseFixture.Stream(body); var handler = Handler(stream); var factory = new SpeechTestFactory(handler);
        var adapter = new OpenAiSpeechAdapter(factory, SpeechFixture.Options); await using var client = SpeechFixture.Build(adapter);
        List<SpeechChunk> received = [];
        var error = await Record.ExceptionAsync(async () =>
        {
            var source = direct ? adapter.StreamNativeAsync(SpeechSseFixture.Request(), TestContext.Current.CancellationToken)
                : client.Audio.Speech.StreamNativeAsync(SpeechSseFixture.Request(), TestContext.Current.CancellationToken);
            await foreach (var chunk in source) received.Add(chunk);
        });
        if (kind == "sentinel") Assert.IsAssignableFrom<JsonException>(error); else Assert.IsType<InvalidDataException>(error);
        Assert.All(received, c => Assert.False(c.IsCompleted)); Assert.Equal(kind == "empty" ? 0 : 1, received.Count);
        Assert.True(stream.Disposed); Assert.True(factory.Clients[0].Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>完成后的部分事件或错误 JSON 也要实际读取，不能在 done 处提前跳出。</summary>
    [Theory] [InlineData("data: {")] [InlineData("data: {broken}\n\n")]
    [InlineData("data: [DONE]\n\n")] [InlineData("data: \n\n")]
    public async Task InvalidTail_InvalidatesHeldTerminal(string tail)
    {
        var body = SpeechSseFixture.Event(SpeechSseFixture.Delta) + SpeechSseFixture.Event(SpeechSseFixture.Done) + tail;
        var adapter = new OpenAiSpeechAdapter(new SpeechTestFactory(Handler(SpeechSseFixture.Stream(body))), SpeechFixture.Options);
        List<SpeechChunk> seen = [];
        var error = await Record.ExceptionAsync(async () =>
        { await foreach (var chunk in adapter.StreamNativeAsync(SpeechSseFixture.Request(), TestContext.Current.CancellationToken)) seen.Add(chunk); });
        if (tail == "data: {") Assert.IsType<InvalidDataException>(error); else Assert.IsAssignableFrom<JsonException>(error);
        Assert.Single(seen); Assert.All(seen, c => Assert.False(c.IsCompleted));
    }

    /// <summary>错误事件无论在 done 前后都保留原始正文和实际 HTTP 状态，聚合不成功。</summary>
    [Theory] [InlineData(false, "typed")] [InlineData(true, "typed")]
    [InlineData(false, "error-field")] [InlineData(true, "error-field")]
    [InlineData(false, "event-name")] [InlineData(true, "event-name")]
    public async Task NativeError_PreservesRawBodyWithoutEarlySuccess(bool afterDone, string kind)
    {
        var json = kind switch { "typed" => "{\"type\":\"error\",\"message\":\"fixture failure\",\"future\":[null,false]}",
            "error-field" => "{\"error\":{\"message\":\"fixture failure\",\"code\":\"test\"}}", _ => "{\"message\":\"fixture failure\"}" };
        var body = SpeechSseFixture.Event(SpeechSseFixture.Delta) + (afterDone ? SpeechSseFixture.Event(SpeechSseFixture.Done) : "")
            + SpeechSseFixture.Event(json, kind == "event-name" ? "error" : null);
        var handler = Handler(SpeechSseFixture.Stream(body)); await using var client = SpeechFixture.Build(new(new SpeechTestFactory(handler), SpeechFixture.Options));
        var error = await Assert.ThrowsAsync<SpeechUpstreamException>(() => client.Audio.Speech.GenerateNativeAsync(SpeechSseFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(200, error.Code); Assert.Equal(json, error.ResponseBody); Assert.Equal(1, handler.Calls);
    }

    /// <summary>部分音频后读取失败及 done 后释放失败，都不能被已有完成事件掩盖。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task ReadOrDisposalFailure_WithholdsCompletion(bool direct, bool disposal)
    {
        var prefix = SpeechSseFixture.Event(SpeechSseFixture.Delta); var body = prefix + SpeechSseFixture.Event(SpeechSseFixture.Done);
        var prefixLength = Encoding.UTF8.GetByteCount(prefix);
        var stream = new SpeechReadStream(Encoding.UTF8.GetBytes(body))
        { FragmentSize = prefixLength, FailAfterBytes = disposal ? null : prefixLength, FailDispose = disposal };
        var factory = new SpeechTestFactory(Handler(stream)); var adapter = new OpenAiSpeechAdapter(factory, SpeechFixture.Options);
        await using var client = SpeechFixture.Build(adapter); List<SpeechChunk> seen = [];
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            var source = direct ? adapter.StreamNativeAsync(SpeechSseFixture.Request(), TestContext.Current.CancellationToken)
                : client.Audio.Speech.StreamNativeAsync(SpeechSseFixture.Request(), TestContext.Current.CancellationToken);
            await foreach (var chunk in source) seen.Add(chunk);
        });
        Assert.Single(seen); Assert.False(seen[0].IsCompleted); Assert.Equal(new byte[] { 0, 255, 128 }, seen[0].Audio.ToArray());
        Assert.True(stream.Disposed); Assert.True(factory.Clients[0].Disposed);
    }

    /// <summary>声明长度对应 UTF-8 原始传输字节，不能用解码音频长度替代。</summary>
    [Theory] [InlineData(-1)] [InlineData(1)]
    public async Task LengthMismatch_RejectsSuccess(int adjustment)
    {
        var wire = SpeechSseFixture.Wire(); var stream = SpeechSseFixture.Stream(wire);
        var handler = new StubHttpHandler((_, _) => Task.FromResult(SpeechSseFixture.Response(stream, Encoding.UTF8.GetByteCount(wire) + adjustment)));
        var adapter = new OpenAiSpeechAdapter(new SpeechTestFactory(handler), SpeechFixture.Options);
        List<SpeechChunk> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { await foreach (var chunk in adapter.StreamNativeAsync(SpeechSseFixture.Request(), TestContext.Current.CancellationToken)) seen.Add(chunk); });
        Assert.All(seen, c => Assert.False(c.IsCompleted)); Assert.True(stream.Disposed);
    }

    /// <summary>部分音频后或 EOF 窗口取消不能生成成功终态。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CancellationAfterAudioOrEof_WithholdsCompletion(bool eof)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var prefix = SpeechSseFixture.Event(SpeechSseFixture.Delta); var body = prefix + SpeechSseFixture.Event(SpeechSseFixture.Done);
        var stream = new SpeechReadStream(Encoding.UTF8.GetBytes(body)) { FragmentSize = Encoding.UTF8.GetByteCount(prefix), OnEof = eof ? cancellation.Cancel : null };
        var factory = new SpeechTestFactory(Handler(stream)); var adapter = new OpenAiSpeechAdapter(factory, SpeechFixture.Options); List<SpeechChunk> seen = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { await foreach (var chunk in adapter.StreamNativeAsync(SpeechSseFixture.Request(), cancellation.Token)) { seen.Add(chunk); if (!eof) cancellation.Cancel(); } });
        Assert.Single(seen); Assert.False(seen[0].IsCompleted); Assert.True(stream.Disposed); Assert.True(factory.Clients[0].Disposed);
    }

    /// <summary>消费者在第一个音频增量停止时不继续读取或捏造完成。</summary>
    [Fact]
    public async Task EarlyStop_DisposesWithoutDraining()
    {
        var prefix = SpeechSseFixture.Event(SpeechSseFixture.Delta); var body = prefix + SpeechSseFixture.Event(SpeechSseFixture.Done);
        var stream = new SpeechReadStream(Encoding.UTF8.GetBytes(body)) { FragmentSize = Encoding.UTF8.GetByteCount(prefix) };
        var factory = new SpeechTestFactory(Handler(stream)); var adapter = new OpenAiSpeechAdapter(factory, SpeechFixture.Options);
        await using (var iterator = adapter.StreamNativeAsync(SpeechSseFixture.Request(), TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        { Assert.True(await iterator.MoveNextAsync()); Assert.False(iterator.Current.IsCompleted); }
        Assert.Equal(1, stream.Reads); Assert.False(stream.Eof); Assert.True(stream.Disposed); Assert.True(factory.Clients[0].Disposed); Assert.False(factory.Disposed);
    }

    /// <summary>不访问真实模型，所有分片与尾部行为由替代处理器提供。</summary>
    private static StubHttpHandler Handler(SpeechReadStream stream) => new((_, _) => Task.FromResult(SpeechSseFixture.Response(stream)));
}
