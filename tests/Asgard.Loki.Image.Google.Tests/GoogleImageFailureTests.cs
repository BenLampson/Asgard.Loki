using System.Net;
using System.Text;
using Xunit;

namespace Asgard.Loki.Image.Google.Tests;

/// <summary>原生图片 HTTP、字段身份与流式终态的必要离线风险测试。</summary>
public sealed class GoogleImageFailureTests
{
    /// <summary>HTTP 错误和成功状态中的原生 error 均保留原始正文且不重试。</summary>
    [Theory]
    [InlineData(200)] [InlineData(429)]
    public async Task HttpAndNativeErrorsKeepOriginalBody(int status)
    {
        var data = GoogleFixture.Part("error"); using var factory = new GoogleTestFactory { OnSend = (_, _) => Task.FromResult(GoogleFixture.Json(data, (HttpStatusCode)status)) };
        var error = await Assert.ThrowsAsync<ImageUpstreamException>(() => new GoogleImageAdapter(factory, GoogleFixture.Options()).GenerateNativeAsync(GoogleFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.Equal(data.GetRawText(), error.ResponseBody); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }
    /// <summary>不能在正文覆盖路径模型、显式方法或发送空 contents；超限在网络前失败。</summary>
    [Theory]
    [InlineData("model")] [InlineData("stream")] [InlineData("contents")] [InlineData("limit")]
    public void InvalidNativeRequestsFailBeforeNetwork(string fault)
    {
        using var factory = new GoogleTestFactory(); var request = GoogleFixture.Request();
        if (fault == "contents") request.Parameters["contents"] = new System.Text.Json.Nodes.JsonArray();
        else if (fault is "model" or "stream") request.Parameters[fault] = "override";
        Assert.Throws<ArgumentException>(() => { _ = new GoogleImageAdapter(factory, GoogleFixture.Options(requestLimit: fault == "limit" ? 1 : 32768)).GenerateNativeAsync(request, TestContext.Current.CancellationToken); }); Assert.Equal(0, factory.Creates);
    }
    /// <summary>完整目标模型必须是单个原生 ID，不将路径前缀或点段当作模型目录。</summary>
    [Theory]
    [InlineData("models/a")] [InlineData("..")] [InlineData("a?key=bad")]
    public void ModelPathIdentityIsExplicit(string model)
    {
        using var factory = new GoogleTestFactory(); Assert.Throws<ArgumentException>(() => new GoogleImageAdapter(factory, GoogleFixture.Options(model: model))); Assert.Equal(0, factory.Creates);
    }
    /// <summary>断流、候选串线、错误字节/长度及重复字段不能提前发布任何原生终态。</summary>
    [Theory]
    [InlineData("missing")] [InlineData("association")] [InlineData("duplicate-index")]
    [InlineData("utf8")] [InlineData("duplicate-json")] [InlineData("length-short")] [InlineData("length-long")] [InlineData("limit")]
    public async Task BrokenStreamCannotReleaseSuccessfulTerminal(string fault)
    {
        var bytes = GoogleFixture.Bytes("gemini-images.sse"); var text = Encoding.UTF8.GetString(bytes);
        bytes = fault switch
        {
            "missing" => Encoding.UTF8.GetBytes(text.Replace("\"finishReason\":\"IMAGE_SAFETY\"", "\"finishReason\":\"FINISH_REASON_UNSPECIFIED\"")),
            "association" => Encoding.UTF8.GetBytes(text.Replace("\"index\":2,\"finishReason\"", "\"index\":99,\"finishReason\"")),
            "duplicate-index" => Encoding.UTF8.GetBytes(text.Replace("\"index\":7,\"content\":{\"parts\":[{\"text\":\"second\"}", "\"index\":2,\"content\":{\"parts\":[{\"text\":\"second\"}")),
            "utf8" => [.. bytes, 0xff],
            "duplicate-json" => [.. bytes, .. Encoding.UTF8.GetBytes("data: {\"future\":null,\"future\":false}\n\n")],
            _ => bytes
        };
        using var factory = new GoogleTestFactory { OnSend = (_, _) =>
        {
            var response = GoogleFixture.Sse(bytes);
            if (fault.StartsWith("length-", StringComparison.Ordinal)) response.Content.Headers.ContentLength = bytes.Length + (fault == "length-short" ? -1 : 1);
            return Task.FromResult(response);
        } };
        var seen = new List<ImageGenerationEvent>(); var adapter = new GoogleImageAdapter(factory, GoogleFixture.Options(responseLimit: fault == "limit" ? 10 : 65536));
        await Assert.ThrowsAsync(fault == "utf8" ? typeof(DecoderFallbackException) : typeof(InvalidDataException), async () =>
        { await foreach (var item in adapter.StreamNativeAsync(GoogleFixture.Request(), TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.DoesNotContain(seen, item => item.Data.TryGetProperty("candidates", out var candidates) && candidates.EnumerateArray().Any(candidate => candidate.TryGetProperty("finishReason", out _)));
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>已经完成的候选不能再次发送内容或终态。</summary>
    [Theory]
    [InlineData("content")] [InlineData("finish")]
    public async Task CandidateCannotContinueOrFinishAgain(string fault)
    {
        var tail = fault == "content" ? "{\"candidates\":[{\"index\":7,\"content\":{\"parts\":[{\"text\":\"late\"}]}}]}" : "{\"candidates\":[{\"index\":7,\"finishReason\":\"STOP\"}]}";
        var bytes = GoogleFixture.Bytes("gemini-images.sse").Concat(Encoding.UTF8.GetBytes("data: " + tail + "\n\n")).ToArray();
        using var factory = new GoogleTestFactory { OnSend = (_, _) => Task.FromResult(GoogleFixture.Sse(bytes)) };
        await Assert.ThrowsAsync<InvalidDataException>(() => GoogleFixture.Collect(new GoogleImageAdapter(factory, GoogleFixture.Options()).StreamNativeAsync(GoogleFixture.Request(), TestContext.Current.CancellationToken))); Assert.Equal(1, factory.Calls);
    }
    /// <summary>晚到的 API 错误保留原始事件身份，且不能先放行成功终态。</summary>
    [Fact]
    public async Task LateNativeErrorKeepsBodyAndSseIdentity()
    {
        var text = "event: error\nid: late-error\ndata: " + GoogleFixture.Part("error").GetRawText().Replace("\r", "").Replace("\n", "") + "\n\n";
        var bytes = GoogleFixture.Bytes("gemini-images.sse").Concat(Encoding.UTF8.GetBytes(text)).ToArray(); var seen = new List<ImageGenerationEvent>();
        using var factory = new GoogleTestFactory { OnSend = (_, _) => Task.FromResult(GoogleFixture.Sse(bytes)) };
        var error = await Assert.ThrowsAsync<ImageUpstreamException>(async () => { await foreach (var item in new GoogleImageAdapter(factory, GoogleFixture.Options()).StreamNativeAsync(GoogleFixture.Request(), TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.Equal("late-error", error.GenerationEvent!.EventId); Assert.Equal("RESOURCE_EXHAUSTED", error.GenerationEvent.Data.GetProperty("error").GetProperty("status").GetString()); Assert.Single(seen);
    }
    /// <summary>完整 HTTP 生命周期的释放失败仍使流失败，不发布已缓存的终态。</summary>
    [Fact]
    public async Task DisposalFailureWithholdsTerminalAndUsageTail()
    {
        using var factory = new GoogleTestFactory { OnSend = (_, _) => Task.FromResult(GoogleFixture.Sse(GoogleFixture.Bytes("gemini-images.sse"))), OnDisposeClient = () => throw new IOException("late disposal") };
        var seen = new List<ImageGenerationEvent>(); await Assert.ThrowsAsync<IOException>(async () =>
        { await foreach (var item in new GoogleImageAdapter(factory, GoogleFixture.Options()).StreamNativeAsync(GoogleFixture.Request(), TestContext.Current.CancellationToken)) seen.Add(item); }); Assert.Single(seen);
    }
    /// <summary>预取消不创建 HTTP 客户端，JSON 不替代显式原生 SSE。</summary>
    [Fact]
    public async Task CancellationAndJsonStreamFallbackDoNotStartAnotherCall()
    {
        using var factory = new GoogleTestFactory(); using var canceled = new CancellationTokenSource(); canceled.Cancel(); var adapter = new GoogleImageAdapter(factory, GoogleFixture.Options());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GenerateNativeAsync(GoogleFixture.Request(), canceled.Token)); Assert.Equal(0, factory.Creates);
        await Assert.ThrowsAsync<InvalidDataException>(() => GoogleFixture.Collect(adapter.StreamNativeAsync(GoogleFixture.Request(), TestContext.Current.CancellationToken))); Assert.Equal(1, factory.Calls);
    }
}
