using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.Ali.Tests.WanFixture;

namespace Asgard.Loki.Video.Ali.Tests;

/// <summary>万相任务身份、响应边界与完整资源释放的离线回归。</summary>
public sealed class AliVideoBoundaryTests
{
    /// <summary>错误任务、缺失状态或不完整成功结果均为协议错误，不能交付成功快照。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"output\":[]}")]
    [InlineData("{\"output\":{\"task_id\":\"other\",\"task_status\":\"PENDING\"}}")]
    [InlineData("{\"output\":{\"task_id\":\"wan-fixture-01\"}}")]
    [InlineData("{\"output\":{\"task_id\":\"wan-fixture-01\",\"task_status\":false}}")]
    [InlineData("{\"output\":{\"task_id\":\"wan-fixture-01\",\"task_status\":\"SUCCEEDED\"}}")]
    [InlineData("{\"output\":{\"task_id\":\"wan-fixture-01\",\"task_status\":\"SUCCEEDED\",\"video_url\":null}}")]
    public async Task InvalidNativeResult_FailsExplicitly(string body)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(body))); var adapter = new AliVideoAdapter(new StubHttpClientFactory(handler), Options());
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetAsync("wan-fixture-01", TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>重复属性、非法编码、无效字符串、数组和截断 JSON 都不能被忽略或修复。</summary>
    [Theory] [InlineData("duplicate")] [InlineData("utf8")] [InlineData("escape")] [InlineData("array")] [InlineData("truncated")] [InlineData("media-type")]
    public async Task InvalidWireBytesAndMediaType_FailExplicitly(string kind)
    {
        var bytes = kind switch
        {
            "duplicate" => Encoding.UTF8.GetBytes("{\"output\":{},\"output\":{}}"), "utf8" => new byte[] { 0xff },
            "escape" => Encoding.UTF8.GetBytes("{\"output\":{\"task_id\":\"wan-fixture-01\",\"task_status\":\"PENDING\"},\"Future\":\"\\uD800\"}"),
            "array" => Encoding.UTF8.GetBytes("[]"), "truncated" => Encoding.UTF8.GetBytes("{\"output\":"), _ => Encoding.UTF8.GetBytes(Ack().GetRawText())
        };
        using var handler = new StubHttpHandler((_, _) =>
        {
            var content = new ByteArrayContent(bytes); content.Headers.ContentType = new(kind == "media-type" ? "application/jsonp" : "application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        var adapter = new AliVideoAdapter(new StubHttpClientFactory(handler), Options());
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetAsync("wan-fixture-01", TestContext.Current.CancellationToken));
    }
    /// <summary>原生容器校验失败时不产生生成请求，参数对象不能替换为另一协议形状。</summary>
    [Theory] [InlineData("missing")] [InlineData("input")] [InlineData("parameters")]
    public async Task InvalidNativeInput_FailsBeforeHttp(string kind)
    {
        var request = Request();
        if (kind == "missing") request.Parameters.Remove("input");
        else request.Parameters[kind] = new JsonArray();
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var adapter = new AliVideoAdapter(new StubHttpClientFactory(handler), Options());
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.SubmitAsync(request, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>快照在网络等待前完成，不透明整数和原生媒体引用不受调用者后续修改影响。</summary>
    [Fact]
    public async Task DirectCall_SnapshotsBeforeWait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHttpHandler(async (message, ct) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(ct); var wire = JsonNode.Parse(await message.Content!.ReadAsStringAsync(ct))!;
            Assert.Equal("A fictional paper boat on a lake. 紙船🌊", wire["input"]!["prompt"]!.GetValue<string>());
            Assert.Equal(9007199254740993L, wire["parameters"]!["Future"]!["large"]!.GetValue<long>()); return Json(Ack());
        });
        var adapter = new AliVideoAdapter(new StubHttpClientFactory(handler), Options()); var request = Request();
        var pending = adapter.SubmitAsync(request, TestContext.Current.CancellationToken); await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        request.Parameters.Clear(); release.SetResult(); await pending; Assert.Equal(1, handler.Calls);
    }
    /// <summary>字节上限、无效 Unicode 和预取消在 IO 前失败，不自动重试。</summary>
    [Fact]
    public async Task LocalLimitsEncodingAndCancellation_DoNotSend()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new StubHttpClientFactory(handler);
        var adapter = new AliVideoAdapter(factory, Options(requestLimit: 10));
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.SubmitAsync(Request(), TestContext.Current.CancellationToken));
        var request = Request(); request.Parameters["Future"]!["text"] = "\ud800";
        await Assert.ThrowsAsync<EncoderFallbackException>(() => adapter.SubmitAsync(request, TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetAsync("wan-fixture-01", cancelled.Token)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>供应商选项的正文上限传入共享读取器，不能只检查序列化后的结果大小。</summary>
    [Fact]
    public async Task ResponseLimit_IsAppliedToRawBytes()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Ack()))); var adapter = new AliVideoAdapter(new StubHttpClientFactory(handler), Options(responseLimit: 5));
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetAsync("wan-fixture-01", TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>借用工厂不会释放，调用创建的客户端在结果公开前已释放。</summary>
    [Fact]
    public async Task ResourceOwnership_IsExplicit()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Ack()))); using var factory = new AliTestHttpFactory(handler);
        var adapter = new AliVideoAdapter(factory, Options()); await adapter.GetAsync("wan-fixture-01", TestContext.Current.CancellationToken);
        Assert.Equal(1, factory.ClientsDisposed); Assert.Equal(0, factory.Disposals);
    }
    /// <summary>客户端最后释放时的故障或取消不能被完整 JSON 终态掩盖。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FinalDisposalFaultOrCancellation_PreventsResult(bool cancel)
    {
        using var cancellation = new CancellationTokenSource(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(6))));
        using var factory = new AliTestHttpFactory(handler) { OnClientDispose = () => { if (cancel) cancellation.Cancel(); else throw new IOException("fixture disposal"); } };
        var adapter = new AliVideoAdapter(factory, Options());
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetAsync("wan-fixture-01", cancellation.Token));
        else await Assert.ThrowsAsync<IOException>(() => adapter.GetAsync("wan-fixture-01", TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls); Assert.Equal(0, factory.Disposals);
    }
    /// <summary>凭据不会出现在公开配置或身份摘要中；非法认证头在构造时明确拒绝。</summary>
    [Fact]
    public void Credentials_DoNotAppearInOptionsOrIdentity()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new StubHttpClientFactory(handler); var options = Options();
        var adapter = new AliVideoAdapter(factory, options);
        Assert.DoesNotContain(options.ApiKey, JsonSerializer.Serialize(options)); Assert.DoesNotContain(options.ApiKey, adapter.BindingIdentity); Assert.DoesNotContain(options.ApiKey, options.ToString()!);
        var error = Assert.Throws<ArgumentException>(() => new AliVideoAdapter(factory, Options(key: "sensitive\r\nfixture"))); Assert.DoesNotContain("sensitive", error.ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AliVideoAdapter(factory, Options(operation: (AliVideoOperation)999))); Assert.Equal(0, handler.Calls);
    }
}
