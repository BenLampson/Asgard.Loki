using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Image.Volcengine.Tests;

/// <summary>必要的整体错误、终态与资源回归，不与单张图片失败混淆。</summary>
public sealed class ArkImageFailureTests
{
    /// <summary>文档允许内部服务错误终止组图；保留原失败结果，不虚构 summary 或整体成功。</summary>
    [Fact]
    public async Task NativeInternalServiceFailure_CanEndWithoutSyntheticSummary()
    {
        var data = JsonNode.Parse(ArkFixture.Part("group_frames")[1].GetProperty("raw_data").GetString()!)!.AsObject(); data["error"]!["code"] = "InternalServiceError";
        var raw = data.ToJsonString(); using var factory = new ArkTestFactory { OnSend = (_, _) => Task.FromResult(ArkFixture.Sse(new ArkReadStream(Encoding.UTF8.GetBytes("data: " + raw + "\n\n")))) };
        var seen = new List<ImageGenerationEvent>(); await foreach (var item in new VolcengineImageAdapter(factory, ArkFixture.Options()).StreamNativeAsync(ArkFixture.Request(2), TestContext.Current.CancellationToken)) seen.Add(item);
        Assert.Single(seen); Assert.Equal(raw, seen[0].Data.GetRawText()); Assert.Equal("image_generation.partial_failed", seen[0].Type); Assert.Equal(1, factory.Calls);
    }

    /// <summary>坏凭据或本地字节策略在构造/调用前失败，不读取或调用上游。</summary>
    [Theory] [InlineData("key")] [InlineData("request")] [InlineData("response")]
    public void InvalidTarget_FailsWithoutHttp(string kind)
    {
        using var factory = new ArkTestFactory();
        Assert.ThrowsAny<ArgumentException>(() => new VolcengineImageAdapter(factory, ArkFixture.Options(key: kind == "key" ? "bad\nvalue" : "fixture", requestLimit: kind == "request" ? 0 : 1000, maximum: kind == "response" ? 0 : 1000)));
        Assert.Equal(0, factory.Creates);
    }

    /// <summary>未知成员也必须有无歧义 JSON/UTF-8，不能以末值覆盖原生结果。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MalformedNativeBody_IsRejected(bool utf8)
    {
        var bytes = utf8 ? new byte[] { 255 } : Encoding.UTF8.GetBytes("{\"data\":[],\"future\":{\"a\":1,\"a\":2}}");
        using var factory = new ArkTestFactory { OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }) };
        await Assert.ThrowsAsync<InvalidDataException>(() => new VolcengineImageAdapter(factory, ArkFixture.Options()).GenerateNativeAsync(ArkFixture.Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls);
    }

    /// <summary>单图最终结果/组图汇总不能在读取、释放或取消失败之前公开。</summary>
    [Theory] [InlineData(false, "read")] [InlineData(true, "read")] [InlineData(false, "dispose")] [InlineData(true, "dispose")]
    [InlineData(false, "client")] [InlineData(true, "client")] [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    public async Task FinalResourceFailure_DoesNotExposeCallTerminal(bool grouped, string kind)
    {
        using var cancel = new CancellationTokenSource();
        using var source = new ArkReadStream(ArkFixture.Bytes(grouped ? "ark-group.sse" : "ark-single.sse")) { FailRead = kind == "read", FailDispose = kind == "dispose", OnEof = kind == "cancel" ? cancel.Cancel : null };
        using var factory = new ArkTestFactory { OnSend = (_, _) => Task.FromResult(ArkFixture.Sse(source)) };
        if (kind == "client") factory.OnDisposeClient = () => throw new IOException("fixture client failure");
        var seen = new List<ImageGenerationEvent>(); var adapter = new VolcengineImageAdapter(factory, ArkFixture.Options());
        async Task Read() { await foreach (var item in adapter.StreamNativeAsync(ArkFixture.Request(grouped ? 2 : 0), kind == "cancel" ? cancel.Token : TestContext.Current.CancellationToken)) seen.Add(item); }
        if (kind == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Read); else await Assert.ThrowsAsync<IOException>(Read);
        if (!grouped) Assert.Empty(seen); else Assert.DoesNotContain(seen, item => item.Type == "image_generation.completed");
        Assert.True(source.Disposed); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }

    /// <summary>整体原生错误保留实际 HTTP、完整 SSE/JSON/任意字节，不把它变为图片级事件或重试。</summary>
    [Theory] [InlineData("sse")] [InlineData("json")] [InlineData("http")]
    public async Task OverallErrors_RetainOriginalBody(string kind)
    {
        var bytes = kind == "http" ? new byte[] { 255, 0 } : Encoding.UTF8.GetBytes(ArkFixture.Part("error").GetRawText());
        using var factory = new ArkTestFactory { OnSend = (_, _) => Task.FromResult(kind == "sse" ? ArkFixture.Sse(new ArkReadStream(ArkFixture.Bytes("ark-error.sse")))
            : new HttpResponseMessage(kind == "http" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }) };
        var adapter = new VolcengineImageAdapter(factory, ArkFixture.Options());
        async Task Call() { if (kind == "sse") { await foreach (var _ in adapter.StreamNativeAsync(ArkFixture.Request(), TestContext.Current.CancellationToken)) { } } else await adapter.GenerateNativeAsync(ArkFixture.Request(), TestContext.Current.CancellationToken); }
        var error = await Assert.ThrowsAsync<ImageUpstreamException>(Call); Assert.Equal(kind == "http" ? 429 : 200, error.StatusCode);
        if (kind == "sse") { Assert.Equal(ArkFixture.Part("error_frame").GetProperty("raw_data").GetString(), error.GenerationEvent!.Data.GetRawText()); Assert.Equal("original-error", error.GenerationEvent.EventId); Assert.Equal(9, error.GenerationEvent.RetryMilliseconds); }
        else Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal(1, factory.Calls);
    }

    /// <summary>缺失组图结束、重复图片索引/汇总、坏尾部或字节长度均明确失败，不按请求最大数量合成结果。</summary>
    [Theory] [InlineData("missing")] [InlineData("duplicate-index")] [InlineData("duplicate-summary")] [InlineData("tail")] [InlineData("length")] [InlineData("limit")]
    public async Task InvalidGroupStream_FailsWithoutSummarySuccess(string kind)
    {
        var frames = ArkFixture.Part("group_frames"); string Frame(int n) => "data: " + frames[n].GetProperty("raw_data").GetString() + "\n\n";
        var wire = kind switch { "missing" => Frame(0), "duplicate-index" => Frame(0) + Frame(0) + Frame(4), "duplicate-summary" => Frame(4) + Frame(4), "tail" => Frame(4) + "data: {", _ => Encoding.UTF8.GetString(ArkFixture.Bytes("ark-group.sse")) };
        var bytes = Encoding.UTF8.GetBytes(wire); using var source = new ArkReadStream(bytes);
        using var factory = new ArkTestFactory { OnSend = (_, _) => { var response = ArkFixture.Sse(source); if (kind == "length") response.Content.Headers.ContentLength = bytes.Length + 1; return Task.FromResult(response); } };
        var adapter = new VolcengineImageAdapter(factory, ArkFixture.Options(maximum: kind == "limit" ? 1 : 100000)); var seen = new List<ImageGenerationEvent>();
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in adapter.StreamNativeAsync(ArkFixture.Request(2), TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.DoesNotContain(seen, item => item.Type == "image_generation.completed"); Assert.Equal(1, factory.Calls);
    }

    /// <summary>原生流实例不能二次发送；消费方早停释放自己拥有的 HTTP 资源。</summary>
    [Fact]
    public async Task EarlyStopAndSecondEnumeration_DoNotReplay()
    {
        using var source = new ArkReadStream(ArkFixture.Bytes("ark-group.sse")); using var factory = new ArkTestFactory { OnSend = (_, _) => Task.FromResult(ArkFixture.Sse(source)) };
        var stream = new VolcengineImageAdapter(factory, ArkFixture.Options()).StreamNativeAsync(ArkFixture.Request(2), TestContext.Current.CancellationToken);
        await foreach (var item in stream) { Assert.Equal("image_generation.partial_succeeded", item.Type); break; }
        Assert.True(source.Disposed); Assert.Equal(1, factory.Disposals);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await foreach (var _ in stream) { } }); Assert.Equal(1, factory.Calls);
    }
}
