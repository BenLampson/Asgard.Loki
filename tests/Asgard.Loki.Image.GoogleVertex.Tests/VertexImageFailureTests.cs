using System.Net;
using System.Text;
using Xunit;

namespace Asgard.Loki.Image.GoogleVertex.Tests;

/// <summary>HTTP 原生错误、流式关联及终态之后的故障，均不重试或伪造成功。</summary>
public sealed class VertexImageFailureTests
{
    /// <summary>HTTP 与成功状态中的原生 error 都保留完整字节。</summary>
    [Theory]
    [InlineData(200)] [InlineData(403)]
    public async Task NativeErrorIsOriginalAndNotRetried(int status)
    {
        var expected = VertexFixture.Part("error"); using var factory = new VertexTestFactory { OnSend = (_, _) => Task.FromResult(VertexFixture.Json(expected, (HttpStatusCode)status)) };
        var error = await Assert.ThrowsAsync<ImageUpstreamException>(() => new GoogleVertexImageAdapter(factory, VertexFixture.Options(key: true)).GenerateNativeAsync(VertexFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.Equal(expected.GetRawText(), error.ResponseBody); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>原生请求中模型/流开关不能覆盖固定路径，空输入与超限在认证前失败。</summary>
    [Theory]
    [InlineData("model")] [InlineData("stream")] [InlineData("contents")] [InlineData("limit")]
    public void InvalidRequestDoesNotAuthenticate(string fault)
    {
        using var factory = new VertexTestFactory(); using var source = new VertexTestTokenSource(); var request = VertexFixture.Request();
        if (fault == "contents") request.Parameters["contents"] = new System.Text.Json.Nodes.JsonArray(); else if (fault is "model" or "stream") request.Parameters[fault] = "override";
        Assert.Throws<ArgumentException>(() => { _ = new GoogleVertexImageAdapter(factory, source, VertexFixture.Options(requestLimit: fault == "limit" ? 1 : 32768)).GenerateNativeAsync(request, TestContext.Current.CancellationToken); }); Assert.Equal(0, source.Calls); Assert.Equal(0, factory.Creates);
    }
    /// <summary>缺失候选终态、错误关联、重复字段、无效字节/长度不能放行原生终态。</summary>
    [Theory]
    [InlineData("missing")] [InlineData("association")] [InlineData("duplicate")] [InlineData("utf8")]
    [InlineData("length-short")] [InlineData("length-long")] [InlineData("limit")]
    public async Task BrokenStreamWithholdsTerminals(string fault)
    {
        var bytes = VertexFixture.Bytes("vertex-images.sse"); var text = Encoding.UTF8.GetString(bytes);
        bytes = fault switch
        {
            "missing" => Encoding.UTF8.GetBytes(text.Replace("\"finishReason\":\"IMAGE_SAFETY\"", "\"finishReason\":\"FINISH_REASON_UNSPECIFIED\"")),
            "association" => Encoding.UTF8.GetBytes(text.Replace("\"index\":0,\"finishReason\"", "\"index\":99,\"finishReason\"")),
            "duplicate" => [.. bytes, .. Encoding.UTF8.GetBytes("data: {\"future\":null,\"future\":false}\n\n")],
            "utf8" => [.. bytes, 0xff], _ => bytes
        };
        using var factory = new VertexTestFactory { OnSend = (_, _) =>
        { var response = VertexFixture.Sse(bytes); if (fault.StartsWith("length-", StringComparison.Ordinal)) response.Content.Headers.ContentLength = bytes.Length + (fault == "length-short" ? -1 : 1); return Task.FromResult(response); } };
        var seen = new List<ImageGenerationEvent>(); var adapter = new GoogleVertexImageAdapter(factory, VertexFixture.Options(key: true, responseLimit: fault == "limit" ? 10 : 65536));
        await Assert.ThrowsAsync(fault == "utf8" ? typeof(DecoderFallbackException) : typeof(InvalidDataException), async () =>
        { await foreach (var item in adapter.StreamNativeAsync(VertexFixture.Request(), TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.DoesNotContain(seen, item => item.Data.TryGetProperty("candidates", out var candidates) && candidates.EnumerateArray().Any(candidate => candidate.TryGetProperty("finishReason", out _)));
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>晚到的 HTTP 资源、认证来源或原生 API 故障不能交付已缓存的终态。</summary>
    [Theory]
    [InlineData("dispose")] [InlineData("source")]
    public async Task LateOwnershipOrSourceFailureWithholdsTerminals(string fault)
    {
        using var source = new VertexTestTokenSource(); using var factory = new VertexTestFactory { OnSend = (_, _) => Task.FromResult(VertexFixture.Sse(VertexFixture.Bytes("vertex-images.sse"))) };
        factory.OnDisposeClient = () => { if (fault == "dispose") throw new IOException("fixture disposal"); source.BindingIdentity = "changed"; };
        var seen = new List<ImageGenerationEvent>(); var adapter = new GoogleVertexImageAdapter(factory, source, VertexFixture.Options(), new VertexTestTime());
        await Assert.ThrowsAsync(fault == "dispose" ? typeof(IOException) : typeof(InvalidOperationException), async () =>
        { await foreach (var item in adapter.StreamNativeAsync(VertexFixture.Request(), TestContext.Current.CancellationToken)) seen.Add(item); }); Assert.Single(seen); Assert.Equal(1, source.Calls); Assert.Equal(1, factory.Calls);
    }
    /// <summary>晚到 API 错误携带完整事件和原始错误字节，不被此前候选终态遮蔽。</summary>
    [Fact]
    public async Task LateApiErrorKeepsNativeIdentity()
    {
        var text = "event: error\nid: vertex-error\ndata: " + VertexFixture.Part("error").GetRawText().Replace("\r", "").Replace("\n", "") + "\n\n";
        var bytes = VertexFixture.Bytes("vertex-images.sse").Concat(Encoding.UTF8.GetBytes(text)).ToArray(); var seen = new List<ImageGenerationEvent>();
        using var factory = new VertexTestFactory { OnSend = (_, _) => Task.FromResult(VertexFixture.Sse(bytes)) };
        var error = await Assert.ThrowsAsync<ImageUpstreamException>(async () => { await foreach (var item in new GoogleVertexImageAdapter(factory, VertexFixture.Options(key: true)).StreamNativeAsync(VertexFixture.Request(), TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.Single(seen); Assert.Equal("vertex-error", error.GenerationEvent!.EventId); Assert.Equal("PERMISSION_DENIED", error.GenerationEvent.Data.GetProperty("error").GetProperty("status").GetString()); Assert.Equal(200, error.StatusCode);
    }
}
