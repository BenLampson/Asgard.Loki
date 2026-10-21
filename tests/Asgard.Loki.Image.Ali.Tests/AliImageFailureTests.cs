using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Image.Ali.Tests;

/// <summary>请求、原生错误和流式终态的风险回归，不调用真实供应商。</summary>
public sealed class AliImageFailureTests
{
    /// <summary>原生业务错误及 HTTP 错误保留完整正文且不重试。</summary>
    [Theory]
    [InlineData(200)] [InlineData(429)]
    public async Task NativeAndHttpErrorsKeepWholeBodyWithoutRetry(int status)
    {
        var error = AliFixture.Part("error"); using var factory = new AliTestFactory { OnSend = (_, _) => Task.FromResult(AliFixture.Json(error, (HttpStatusCode)status)) };
        var exception = await Assert.ThrowsAsync<ImageUpstreamException>(() => new AliImageAdapter(factory, AliFixture.Options()).GenerateNativeAsync(AliFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(status, exception.StatusCode); Assert.Equal(error.GetRawText(), exception.ResponseBody); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>模式冲突和无效请求在创建 HTTP 调用前失败。</summary>
    [Theory]
    [InlineData("model")] [InlineData("input")] [InlineData("parameters")]
    [InlineData("stream")] [InlineData("interleave")] [InlineData("limit")]
    public void InvalidRequestsFailBeforeNetwork(string error)
    {
        using var factory = new AliTestFactory(); var request = AliFixture.Request();
        switch (error)
        {
            case "model": request.Parameters["MODEL"] = "other"; break;
            case "input": request.Parameters["input"] = null; break;
            case "parameters": request.Parameters["parameters"] = false; break;
            case "stream": request.Parameters["parameters"]!["stream"] = true; break;
            case "interleave": request.Parameters["parameters"]!["enable_interleave"] = true; break;
        }
        var adapter = new AliImageAdapter(factory, AliFixture.Options(requestLimit: error == "limit" ? 1 : 32768));
        if (error == "interleave") Assert.Throws<NotSupportedException>(() => { _ = adapter.GenerateNativeAsync(request, TestContext.Current.CancellationToken); });
        else Assert.Throws<ArgumentException>(() => { _ = adapter.GenerateNativeAsync(request, TestContext.Current.CancellationToken); }); Assert.Equal(0, factory.Calls);
    }
    /// <summary>显式流入口不得静默覆盖冲突的原生模式。</summary>
    [Theory]
    [InlineData("conflict")] [InlineData("interleave")]
    public void ExplicitStreamDoesNotSilentlyRewriteMode(string error)
    {
        using var factory = new AliTestFactory(); var request = AliFixture.Request(streaming: true);
        request.Parameters["parameters"]![error == "conflict" ? "stream" : "enable_interleave"] = false;
        Assert.Throws<ArgumentException>(() => new AliImageAdapter(factory, AliFixture.Options()).StreamNativeAsync(request, TestContext.Current.CancellationToken)); Assert.Equal(0, factory.Calls);
    }
    /// <summary>断流、关联错误、坏尾部及长度错误不能发布成功终态。</summary>
    [Theory]
    [InlineData("truncated")] [InlineData("choice")] [InlineData("request")]
    [InlineData("duplicate")] [InlineData("utf8")] [InlineData("limit")]
    [InlineData("length-short")] [InlineData("length-long")]
    public async Task StreamCannotCompleteWithMissingStopsBrokenAssociationOrBadTail(string error)
    {
        var bytes = AliFixture.Bytes("ali-images.sse"); var text = Encoding.UTF8.GetString(bytes);
        bytes = error switch
        {
            "truncated" => Encoding.UTF8.GetBytes(text[..text.LastIndexOf("event: result", StringComparison.Ordinal)]),
            "choice" => Encoding.UTF8.GetBytes(text.Replace("\"index\":2,\"finish_reason\":\"stop\"", "\"index\":99,\"finish_reason\":\"stop\"")),
            "request" => Encoding.UTF8.GetBytes(text.Replace("\"request_id\":\"stream-id\",\"output\":{\"finished\":true,\"choices\":[{\"index\":7,\"finish_reason\":\"stop\"", "\"request_id\":\"other\",\"output\":{\"finished\":true,\"choices\":[{\"index\":7,\"finish_reason\":\"stop\"")),
            "duplicate" => [.. bytes, .. Encoding.UTF8.GetBytes("data: {\"output\":{},\"output\":{}}\n\n")],
            "utf8" => [.. bytes, 0xff],
            _ => bytes
        };
        using var factory = new AliTestFactory { OnSend = (_, _) =>
        {
            var response = AliFixture.Sse(bytes);
            if (error.StartsWith("length-", StringComparison.Ordinal)) response.Content.Headers.ContentLength = bytes.Length + (error == "length-short" ? -1 : 1);
            return Task.FromResult(response);
        } };
        var adapter = new AliImageAdapter(factory, AliFixture.Options(responseLimit: error == "limit" ? 20 : 65536));
        var seen = new List<ImageGenerationEvent>();
        await Assert.ThrowsAsync(error == "utf8" ? typeof(DecoderFallbackException) : typeof(InvalidDataException), async () => { await foreach (var item in adapter.StreamNativeAsync(AliFixture.Request(streaming: true), TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.DoesNotContain(seen, item => item.Data.GetProperty("output").GetProperty("choices").EnumerateArray().Any(choice => choice.GetProperty("finish_reason").ToString() == "stop"));
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>流错误保留原生事件身份和全部错误字段。</summary>
    [Fact]
    public async Task StreamErrorKeepsOriginalSseIdentityAndWholeNativeError()
    {
        using var factory = new AliTestFactory { OnSend = (_, _) => Task.FromResult(AliFixture.Sse(AliFixture.Bytes("ali-error.sse"))) };
        var adapter = new AliImageAdapter(factory, AliFixture.Options());
        var error = await Assert.ThrowsAsync<ImageUpstreamException>(() => AliFixture.Collect(adapter.StreamNativeAsync(AliFixture.Request(streaming: true), TestContext.Current.CancellationToken)));
        Assert.Equal("failure-id", error.GenerationEvent!.EventId); Assert.Equal("error", error.GenerationEvent.EventName);
        Assert.Equal("DataInspectionFailed", error.GenerationEvent.Data.GetProperty("code").GetString()); Assert.Contains("\"future\":null", error.ResponseBody); Assert.Equal(200, error.StatusCode);
    }
    /// <summary>终态之后的原生错误仍使整次调用失败。</summary>
    [Fact]
    public async Task LateNativeErrorDoesNotReleaseSuccessfulTerminal()
    {
        var bytes = AliFixture.Bytes("ali-images.sse").Concat(AliFixture.Bytes("ali-error.sse")).ToArray();
        using var factory = new AliTestFactory { OnSend = (_, _) => Task.FromResult(AliFixture.Sse(bytes)) }; var seen = new List<ImageGenerationEvent>();
        await Assert.ThrowsAsync<ImageUpstreamException>(async () => { await foreach (var item in new AliImageAdapter(factory, AliFixture.Options()).StreamNativeAsync(AliFixture.Request(streaming: true), TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.Single(seen); Assert.Equal(1, factory.Calls);
    }
    /// <summary>资源释放失败不能放行已暂存的成功终态。</summary>
    [Fact]
    public async Task DisposingClientLateDoesNotReleaseSuccessfulTerminal()
    {
        using var factory = new AliTestFactory { OnSend = (_, _) => Task.FromResult(AliFixture.Sse(AliFixture.Bytes("ali-images.sse"))), OnDisposeClient = () => throw new IOException("fixture late disposal") };
        var seen = new List<ImageGenerationEvent>();
        await Assert.ThrowsAsync<IOException>(async () => { await foreach (var item in new AliImageAdapter(factory, AliFixture.Options()).StreamNativeAsync(AliFixture.Request(streaming: true), TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.Single(seen); Assert.Equal(1, factory.Calls);
    }
    /// <summary>预取消在借用工厂创建客户端前终止。</summary>
    [Fact]
    public async Task CancellationBeforeCallDoesNotCreateClient()
    {
        using var token = new CancellationTokenSource(); token.Cancel(); using var factory = new AliTestFactory();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AliImageAdapter(factory, AliFixture.Options()).GenerateNativeAsync(AliFixture.Request(), token.Token)); Assert.Equal(0, factory.Creates);
    }
}
