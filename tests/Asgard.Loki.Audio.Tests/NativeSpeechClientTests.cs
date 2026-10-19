using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Audio.Tests;

/// <summary>原生音频核心仅承担快照、完整目标选择、字节聚合和终态，不解释供应商字段。</summary>
public sealed class NativeSpeechClientTests
{
    /// <summary>自定义 voice 对象、未知字段和 JSON 类型完整保留，聚合结果不猜测输出格式。</summary>
    [Fact]
    public async Task GenerateNative_PreservesNativeFieldsAndOrderedAudio()
    {
        var adapter = new StubNativeSpeechAdapter([Chunk([1, 2], false), Chunk([3], true)]);
        await using var client = Build(adapter); var request = Request(); var before = JsonSerializer.Serialize(request);
        var result = await client.Audio.Speech.GenerateNativeAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Audio.ToArray()); Assert.Equal(2, result.Frames.Count);
        Assert.True(result.Frames[1].GetProperty("vendor_done").GetBoolean()); Assert.False(result.Frames[0].GetProperty("future").GetBoolean());
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.Equal(before, JsonSerializer.Serialize(adapter.LastRequest));
        Assert.Equal(1, adapter.Calls); Assert.True(adapter.IteratorDisposed);
    }

    /// <summary>在接收完成块前源枚举必须已正常结束和释放，共用语音路径也遵循相同规则。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Completion_IsReleasedOnlyAfterSourceEnds(bool native)
    {
        var adapter = new StubNativeSpeechAdapter([Chunk([1], false), Chunk([2], true)]); await using var client = Build(adapter);
        var chunks = new List<SpeechChunk>();
        await foreach (var chunk in Stream(client.Audio.Speech, native, TestContext.Current.CancellationToken))
        { if (chunk.IsCompleted) Assert.True(adapter.IteratorDisposed); chunks.Add(chunk); }
        Assert.Equal(new byte[] { 1, 2 }, chunks.SelectMany(c => c.Audio.ToArray())); Assert.True(chunks[^1].IsCompleted); Assert.Equal(1, adapter.Calls);
    }

    /// <summary>晚到或重复完成块、缺少终态都不能向调用者先发送成功终态。</summary>
    [Theory] [InlineData(false, "late")] [InlineData(true, "late")] [InlineData(false, "duplicate")]
    [InlineData(true, "duplicate")] [InlineData(false, "missing")] [InlineData(true, "missing")]
    [InlineData(false, "empty")] [InlineData(true, "empty")]
    public async Task InvalidTerminalSequence_NeverPublishesSuccess(bool native, string kind)
    {
        var source = kind switch
        {
            "late" => new[] { Chunk([1], false), Chunk([2], true), Chunk([3], false) },
            "duplicate" => [Chunk([1], true), Chunk([2], true)], "missing" => [Chunk([1], false)], _ => Array.Empty<SpeechChunk>()
        };
        var adapter = new StubNativeSpeechAdapter(source); await using var client = Build(adapter); var received = new List<SpeechChunk>();
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var chunk in Stream(client.Audio.Speech, native, TestContext.Current.CancellationToken)) received.Add(chunk); });
        Assert.All(received, c => Assert.False(c.IsCompleted)); Assert.True(adapter.IteratorDisposed); Assert.Equal(1, adapter.Calls);
    }

    /// <summary>完成后的尾部异常或取消不能被已有完成标记掩盖。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task TailFailureOrCancellation_NeverPublishesSuccess(bool native, bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var adapter = new StubNativeSpeechAdapter([Chunk([1], false), Chunk([2], true)])
        { TailError = cancel ? null : new IOException("source failed"), OnTail = cancel ? cancellation.Cancel : null };
        await using var client = Build(adapter); var received = new List<SpeechChunk>();
        var task = CollectAsync(Stream(client.Audio.Speech, native, cancellation.Token), received);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); else await Assert.ThrowsAsync<IOException>(() => task);
        Assert.All(received, c => Assert.False(c.IsCompleted)); Assert.True(adapter.IteratorDisposed);
    }

    /// <summary>原生字典与嵌套 JSON 在派发前独立复制，源文档可在目标调用时释放。</summary>
    [Fact]
    public async Task NativeParameters_AreIndependentSnapshots()
    {
        using var document = JsonDocument.Parse("""{"id":"voice_custom","future":null}"""); var request = Request(); request.Parameters["voice"] = document.RootElement;
        var adapter = new StubNativeSpeechAdapter([Chunk([1], true)])
        {
            OnRequest = snapshot =>
            {
                request.Parameters["voice"] = JsonSerializer.SerializeToElement("changed"); document.Dispose();
                Assert.Equal("voice_custom", snapshot.Parameters["voice"].GetProperty("id").GetString());
                Assert.Equal(JsonValueKind.Null, snapshot.Parameters["voice"].GetProperty("future").ValueKind);
            }
        };
        await using var client = Build(adapter); await client.Audio.Speech.GenerateNativeAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal("voice_custom", adapter.LastRequest!.Parameters["voice"].GetProperty("id").GetString());
    }

    /// <summary>保留音频能力的路由快照，不因初始化后的集合修改改变完整目标。</summary>
    [Fact]
    public async Task Registration_SnapshotsNativeTargets()
    {
        var adapter = new StubNativeSpeechAdapter([Chunk([1], true)]); var targets = new List<ISpeechAdapter> { adapter };
        var routes = new List<SpeechRoute> { new() { Model = "voice", Targets = targets } };
        await using var client = new LokiClientBuilder().AddAudio(new() { SpeechRoutes = routes }).Build(); targets.Clear(); routes.Clear();
        var result = await client.Audio.Speech.GenerateNativeAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1 }, result.Audio.ToArray()); Assert.Equal(1, adapter.Calls);
    }

    /// <summary>原生路由不筛选或跳过不支持的目标，拒绝之前不能推进调度或调用任何目标。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnsupportedRoute_DoesNotFilterOrFailOver(bool mixed)
    {
        var native = new StubNativeSpeechAdapter([Chunk([1], true)]); var legacy = new StubSpeechAdapter([Chunk([2], true)]);
        await using var client = Build(mixed ? [native, legacy] : [legacy]);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.Audio.Speech.GenerateNativeAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(0, native.Calls);
        if (mixed)
        {
            var common = await client.Audio.Speech.GenerateAsync(CommonRequest(), TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 1 }, common.Audio.ToArray()); Assert.Equal(1, native.Calls);
        }
    }

    /// <summary>基本原生请求错误在选择目标前失败，不解释或修复供应商字段。</summary>
    [Theory] [InlineData("request")] [InlineData("model")] [InlineData("parameters")] [InlineData("reserved")] [InlineData("undefined")]
    public async Task InvalidNativeRequest_DoesNotCallTarget(string kind)
    {
        var request = kind switch
        {
            "request" => null!, "model" => Request() with { Model = " " }, "parameters" => Request() with { Parameters = null! },
            "reserved" => Request() with { Parameters = new() { ["model"] = JsonSerializer.SerializeToElement("override") } },
            _ => Request() with { Parameters = new() { ["input"] = default } }
        };
        var adapter = new StubNativeSpeechAdapter([Chunk([1], true)]); await using var client = Build(adapter);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.Audio.Speech.GenerateNativeAsync(request, TestContext.Current.CancellationToken)); Assert.Equal(0, adapter.Calls);
    }

    /// <summary>未知路由及预取消不调用目标；预取消同样保护既有便捷入口。</summary>
    [Theory] [InlineData("route")] [InlineData("native-cancel")] [InlineData("common-cancel")]
    public async Task UnknownRouteAndPreCancellation_DoNotCallTarget(string kind)
    {
        var adapter = new StubNativeSpeechAdapter([Chunk([1], true)]); await using var client = Build(adapter);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        if (kind == "route") await Assert.ThrowsAsync<InvalidOperationException>(() => client.Audio.Speech.GenerateNativeAsync(Request() with { Model = "unknown" }, TestContext.Current.CancellationToken));
        else if (kind == "native-cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Audio.Speech.GenerateNativeAsync(Request(), cancellation.Token));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Audio.Speech.GenerateAsync(CommonRequest(), cancellation.Token));
        Assert.Equal(0, adapter.Calls);
    }

    /// <summary>消费者提前停止会释放源枚举，既有入口也不会触发额外目标或伪造终态。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task EarlyDisposal_ReleasesSource(bool native)
    {
        var adapter = new StubNativeSpeechAdapter([Chunk([1], false), Chunk([2], true)]); await using var client = Build(adapter);
        await using (var enumerator = Stream(client.Audio.Speech, native, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        { Assert.True(await enumerator.MoveNextAsync()); Assert.False(enumerator.Current.IsCompleted); }
        Assert.True(adapter.IteratorDisposed); Assert.Equal(1, adapter.Calls);
    }

    /// <summary>既有客户端实现无需新增成员即可编译，并对新能力给出明确不支持结果。</summary>
    [Fact]
    public async Task LegacyClient_RemainsSourceCompatible()
    {
        ISpeechClient client = new LegacySpeechClient();
        Assert.Equal(new byte[] { 1 }, (await client.GenerateAsync(CommonRequest(), TestContext.Current.CancellationToken)).Audio.ToArray());
        await Assert.ThrowsAsync<NotSupportedException>(() => client.GenerateNativeAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Throws<NotSupportedException>(() => client.StreamNativeAsync(Request(), TestContext.Current.CancellationToken));
    }

    /// <summary>音频核心不为原生供应商发明 input、voice 或格式默认值。</summary>
    [Fact]
    public async Task NativeCore_DoesNotInventVendorFields()
    {
        var adapter = new StubNativeSpeechAdapter([Chunk([1], true)]); await using var client = Build(adapter);
        await client.Audio.Speech.GenerateNativeAsync(new() { Model = "voice" }, TestContext.Current.CancellationToken);
        Assert.Empty(adapter.LastRequest!.Parameters);
    }

    /// <summary>一次失败不会调用其他目标；后续独立请求才按已配置调度次序选择下一目标。</summary>
    [Fact]
    public async Task NativeFailure_DoesNotRetryOrSwitchTarget()
    {
        using var first = new StubNativeSpeechAdapter([]) { Id = "first", TailError = new IOException("upstream failed") };
        using var second = new StubNativeSpeechAdapter([Chunk([2], true)]) { Id = "second" };
        await using var client = Build(first, second);
        await Assert.ThrowsAsync<IOException>(() => client.Audio.Speech.GenerateNativeAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(1, first.Calls); Assert.Equal(0, second.Calls);
        var next = await client.Audio.Speech.GenerateNativeAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 2 }, next.Audio.ToArray()); Assert.Equal(1, first.Calls); Assert.Equal(1, second.Calls);
    }

    /// <summary>客户端释放后适配器仍归调用者所有，核心不会通过可选接口扩大释放边界。</summary>
    [Fact]
    public async Task NativeAdapter_RemainsBorrowedAfterClientDisposal()
    {
        using var adapter = new StubNativeSpeechAdapter([Chunk([1], true)]); var client = Build(adapter);
        await client.Audio.Speech.GenerateNativeAsync(Request(), TestContext.Current.CancellationToken); await client.DisposeAsync();
        Assert.False(adapter.Disposed); var received = new List<SpeechChunk>();
        await CollectAsync(adapter.StreamNativeAsync(Request(), TestContext.Current.CancellationToken), received);
        Assert.True(Assert.Single(received).IsCompleted); Assert.Equal(2, adapter.Calls);
    }

    /// <summary>源释放失败时同样不能把暂存的完成标记发送为成功。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SourceDisposalFailure_NeverPublishesSuccess(bool native)
    {
        var adapter = new DisposalFaultSpeechAdapter([Chunk([1], false), Chunk([2], true)]);
        await using var client = Build(adapter); var received = new List<SpeechChunk>();
        await Assert.ThrowsAsync<IOException>(() => CollectAsync(Stream(client.Audio.Speech, native, TestContext.Current.CancellationToken), received));
        Assert.All(received, c => Assert.False(c.IsCompleted)); Assert.Equal(1, adapter.DisposeCalls);
    }

    /// <summary>对外路由只依赖本能力的适配器接口。</summary>
    private static Asgard.Loki.Abstractions.ILokiClient Build(params ISpeechAdapter[] targets) => new LokiClientBuilder().AddAudio(new()
    { SpeechRoutes = [new() { Model = "voice", Targets = targets }] }).Build();
    /// <summary>保留自定义声音对象、原生输出格式与未来字段。</summary>
    private static NativeSpeechRequest Request() => JsonSerializer.Deserialize<NativeSpeechRequest>("""{"model":"voice","input":"你好😀","voice":{"id":"voice_custom"},"response_format":"pcm","future":false,"future_null":null}""")!;
    /// <summary>既有公共便捷语音请求。</summary>
    private static SpeechRequest CommonRequest() => new() { Model = "voice", Text = "hello", Voice = "alloy" };
    /// <summary>能力测试的供应商元数据，不冒充真实 OpenAI 事件。</summary>
    private static SpeechChunk Chunk(byte[] bytes, bool completed) => new(bytes, completed, JsonSerializer.SerializeToElement(new { vendor_done = completed, future = false }), "native");
    /// <summary>从公共入口消费原生或便捷流。</summary>
    private static IAsyncEnumerable<SpeechChunk> Stream(ISpeechClient client, bool native, CancellationToken token)
        => native ? client.StreamNativeAsync(Request(), token) : client.StreamAsync(CommonRequest(), token);
    /// <summary>消费所有块，以观察尾部错误。</summary>
    private static async Task CollectAsync(IAsyncEnumerable<SpeechChunk> source, List<SpeechChunk> received)
    { await foreach (var chunk in source) received.Add(chunk); }
}
