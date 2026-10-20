using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Image.Replicate.Tests;

/// <summary>原生预测错误、明确目标、本地请求/结果和最终 HTTP 所有权边界。</summary>
public sealed class ReplicateImageFailureTests
{
    /// <summary>HTTP 错误保留非 UTF-8 字节，不尝试下载、重试或替换正文。</summary>
    [Fact]
    public async Task HttpErrorsKeepExactBinaryBody()
    {
        byte[] bytes = [255, 0, 254]; using var factory = ReplicateFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new ByteArrayContent(bytes) });
        var error = await Assert.ThrowsAsync<ImageUpstreamException>(() => new ReplicateImageAdapter(factory, ReplicateFixture.Options()).PredictAsync(ReplicateFixture.Native(), TestContext.Current.CancellationToken));
        Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal(502, error.StatusCode); Assert.Equal(1, factory.Calls);
    }
    /// <summary>成功 HTTP 携带原生 API 错误时保留原始正文。</summary>
    [Fact]
    public async Task NativeApiErrorRemainsWhole()
    {
        using var factory = ReplicateFixture.Factory("http_error"); var error = await Assert.ThrowsAsync<ImageUpstreamException>(() => new ReplicateImageAdapter(factory, ReplicateFixture.Options()).PredictAsync(ReplicateFixture.Native(), TestContext.Current.CancellationToken));
        Assert.True(JsonElement.DeepEquals(ReplicateFixture.Part("http_error"), ReplicateFixture.Element(error.ResponseBody))); Assert.Equal(1, factory.Calls);
    }
    /// <summary>公共 input 不允许覆盖提示词、目标或注入第二层 input；无编辑引用也不能静默退化。</summary>
    [Theory]
    [InlineData("Prompt")] [InlineData("model")] [InlineData("input")] [InlineData("missing-edit-image")]
    public void InvalidPublicInputFailsBeforeHttp(string field)
    {
        using var factory = ReplicateFixture.Factory(); var adapter = new ReplicateImageAdapter(factory, ReplicateFixture.Options()); var request = ReplicateFixture.Request();
        if (field == "missing-edit-image") request.Parameters.Remove("image_prompt"); else request.Parameters[field] = JsonSerializer.SerializeToElement("override");
        Assert.Throws<ArgumentException>(() => { _ = adapter.EditAsync(new() { Model = request.Model, Prompt = request.Prompt, Parameters = request.Parameters }, TestContext.Current.CancellationToken); }); Assert.Equal(0, factory.Creates);
    }
    /// <summary>完整模型目标和本地上限在任何 HTTP 调用前验证。</summary>
    [Theory]
    [InlineData("model")] [InlineData("key")] [InlineData("root")] [InlineData("limits")]
    public void InvalidTargetFailsLocally(string kind)
    {
        using var factory = ReplicateFixture.Factory(); var options = ReplicateFixture.Options(model: kind == "model" ? "owner/../other" : "owner/name", key: kind == "key" ? "bad\nkey" : "fixture", root: kind == "root" ? "https://root.example.invalid/?query=1" : "https://root.example.invalid/v1/", uploadLimit: kind == "limits" ? 0 : 1000);
        Assert.ThrowsAny<ArgumentException>(() => new ReplicateImageAdapter(factory, options)); Assert.Equal(0, factory.Creates);
    }
    /// <summary>模型资源不由根参数覆盖，原生 input 必须为明确对象。</summary>
    [Theory]
    [InlineData("{\"input\":{},\"version\":\"override\"}")]
    [InlineData("{\"input\":null}")]
    [InlineData("{\"input\":{},\"Model\":\"override\"}")]
    public void InvalidNativeInputFailsBeforeHttp(string json)
    {
        using var factory = ReplicateFixture.Factory(); var request = new ReplicateNativeImageRequest { PublicModel = "pictures", Parameters = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject() };
        Assert.Throws<ArgumentException>(() => { _ = new ReplicateImageAdapter(factory, ReplicateFixture.Options()).PredictAsync(request, TestContext.Current.CancellationToken); }); Assert.Equal(0, factory.Creates);
    }
    /// <summary>字节上限是实际 UTF-8 正文长度，未知 Unicode 不得有损替换。</summary>
    [Fact]
    public void RequestByteAndEncodingLimitsAreLocal()
    {
        using var factory = ReplicateFixture.Factory(); Assert.Throws<ArgumentException>(() => { _ = new ReplicateImageAdapter(factory, ReplicateFixture.Options(requestLimit: 1)).PredictAsync(ReplicateFixture.Native(), TestContext.Current.CancellationToken); });
        var request = ReplicateFixture.Native(); request.Parameters["future"] = "\ud800";
        Assert.Throws<EncoderFallbackException>(() => { _ = new ReplicateImageAdapter(factory, ReplicateFixture.Options()).PredictAsync(request, TestContext.Current.CancellationToken); }); Assert.Equal(0, factory.Creates);
    }
    /// <summary>无歧义 JSON、原始长度、完整字节及媒体类型均验证，不只验证解析后内容。</summary>
    [Theory]
    [InlineData("bytes")] [InlineData("length")] [InlineData("utf8")] [InlineData("duplicates")] [InlineData("type")]
    public async Task CompleteResponseBoundariesAreEnforced(string kind)
    {
        var bytes = Encoding.UTF8.GetBytes(ReplicateFixture.Part("success").GetRawText()); if (kind == "utf8") bytes = [255];
        if (kind == "duplicates") bytes = Encoding.UTF8.GetBytes("{\"id\":\"a\",\"status\":\"succeeded\",\"future\":1,\"future\":2}");
        using var source = new ReplicateReadStream(bytes); using var factory = ReplicateFixture.Factory(); factory.OnSend = (_, _) =>
        {
            var content = new StreamContent(source); content.Headers.ContentType = new(kind == "type" ? "text/event-stream" : "application/json"); if (kind == "length") content.Headers.ContentLength = bytes.Length + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => new ReplicateImageAdapter(factory, ReplicateFixture.Options(responseLimit: kind == "bytes" ? bytes.Length - 1 : bytes.Length + 100)).PredictAsync(ReplicateFixture.Native(), TestContext.Current.CancellationToken));
        Assert.True(source.Disposed); Assert.Equal(1, factory.Disposals); Assert.Equal(1, factory.Calls);
    }
    /// <summary>最后一次读取、流/客户端释放和取消必须成功后才交付预测快照。</summary>
    [Theory]
    [InlineData("read")] [InlineData("dispose")] [InlineData("client")] [InlineData("cancel")]
    public async Task LateFailureCannotReleasePrediction(string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var source = new ReplicateReadStream(Encoding.UTF8.GetBytes(ReplicateFixture.Part("success").GetRawText()))
        { FailRead = kind == "read", FailDispose = kind == "dispose", OnEof = kind == "cancel" ? cancellation.Cancel : null };
        using var factory = ReplicateFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) });
        if (kind == "client") factory.OnDisposeClient = () => throw new IOException("fixture client dispose");
        if (kind == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReplicateImageAdapter(factory, ReplicateFixture.Options()).PredictAsync(ReplicateFixture.Native(), cancellation.Token));
        else await Assert.ThrowsAsync<IOException>(() => new ReplicateImageAdapter(factory, ReplicateFixture.Options()).PredictAsync(ReplicateFixture.Native(), cancellation.Token));
        Assert.True(source.Disposed); Assert.Equal(1, factory.Disposals); Assert.Equal(1, factory.Calls); Assert.False(factory.Disposed);
    }
}
