using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Image.Midjourney.Tests;

/// <summary>原生错误、完整 JSON/HTTP 边界及最终资源/取消检查。</summary>
public sealed class MidjourneyImageFailureTests
{
    /// <summary>即使错误含 result 任务号，也不能将 queue full 等错误当成接受成功。</summary>
    [Theory]
    [InlineData("queue_full")] [InlineData("sensitive")]
    public async Task NativeFailureRetainsBodyAndNeverRetries(string name)
    {
        using var factory = MidjourneyFixture.Factory(name); var error = await Assert.ThrowsAsync<ImageUpstreamException>(() => new MidjourneyImageClient(factory, MidjourneyFixture.Options()).SubmitAsync(MidjourneyImageOperation.Imagine, MidjourneyFixture.Body(), TestContext.Current.CancellationToken));
        Assert.Equal(200, error.StatusCode); Assert.True(System.Text.Json.JsonElement.DeepEquals(MidjourneyFixture.Part(name), MidjourneyFixture.Element(error.ResponseBody))); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>原生任务不存在的错误不伪装成空的成功任务。</summary>
    [Fact]
    public async Task NativeMissingTaskErrorRemainsWhole()
    {
        using var factory = MidjourneyFixture.Factory("not_found"); var client = new MidjourneyImageClient(factory, MidjourneyFixture.Options());
        var error = await Assert.ThrowsAsync<ImageUpstreamException>(() => client.GetAsync(client.ReferenceTask("task-fixture"), TestContext.Current.CancellationToken)); Assert.Equal(200, error.StatusCode); Assert.Contains("Native task missing", error.ResponseBody); Assert.Equal(1, factory.Calls);
    }
    /// <summary>HTTP 错误保留非 UTF-8 原始字节和实际状态。</summary>
    [Fact]
    public async Task BinaryHttpFailureKeepsExactBytes()
    {
        byte[] bytes = [255, 0, 254]; using var factory = MidjourneyFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new ByteArrayContent(bytes) });
        var error = await Assert.ThrowsAsync<ImageUpstreamException>(() => new MidjourneyImageClient(factory, MidjourneyFixture.Options()).SubmitAsync(MidjourneyImageOperation.Imagine, MidjourneyFixture.Body(), TestContext.Current.CancellationToken)); Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal(502, error.StatusCode); Assert.Equal(1, factory.Calls);
    }
    /// <summary>各原生操作的返回形状不可互换；缺失身份/状态不能靠字符串化或默认值修复。</summary>
    [Theory]
    [InlineData("code")] [InlineData("submit-result")] [InlineData("upload-result")] [InlineData("task-status")]
    public async Task WrongNativeResponseShapeCannotCrossOperationContracts(string kind)
    {
        var raw = kind switch
        {
            "code" => "{\"code\":\"1\",\"result\":\"task-fixture\"}",
            "submit-result" => "{\"code\":1,\"result\":[\"task-fixture\"]}",
            "upload-result" => "{\"code\":1,\"result\":\"task-fixture\"}",
            _ => "{\"id\":\"task-fixture\",\"status\":null}"
        };
        using var factory = MidjourneyFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(MidjourneyFixture.Json(MidjourneyFixture.Element(raw)));
        var client = new MidjourneyImageClient(factory, MidjourneyFixture.Options());
        if (kind == "upload-result") await Assert.ThrowsAsync<InvalidDataException>(() => client.UploadInputsAsync([], TestContext.Current.CancellationToken));
        else if (kind == "task-status") await Assert.ThrowsAsync<InvalidDataException>(() => client.GetAsync(client.ReferenceTask("task-fixture"), TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<InvalidDataException>(() => client.SubmitAsync(MidjourneyImageOperation.Imagine, MidjourneyFixture.Body(), TestContext.Current.CancellationToken));
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>处理器若改变请求目标，不能在另一个代理的原生结果上继续关联本目标任务。</summary>
    [Fact]
    public async Task ChangedResponseTargetFailsBeforeReadingItsBody()
    {
        using var source = new MidjourneyReadStream(Encoding.UTF8.GetBytes(MidjourneyFixture.Part("submitted").GetRawText()));
        using var actual = new HttpRequestMessage(HttpMethod.Post, "http://other.example.invalid/mj/submit/imagine");
        using var factory = MidjourneyFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = actual, Content = new StreamContent(source) });
        await Assert.ThrowsAsync<InvalidDataException>(() => new MidjourneyImageClient(factory, MidjourneyFixture.Options()).SubmitAsync(MidjourneyImageOperation.Imagine, MidjourneyFixture.Body(), TestContext.Current.CancellationToken));
        Assert.False(source.Eof); Assert.True(source.Disposed); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>不支持的枚举、坏身份和本地请求上限在任何工厂调用前失败。</summary>
    [Fact]
    public void InvalidLocalInputDoesNotCreateHttp()
    {
        using var factory = MidjourneyFixture.Factory(); var client = new MidjourneyImageClient(factory, MidjourneyFixture.Options());
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = client.SubmitAsync((MidjourneyImageOperation)999, [], TestContext.Current.CancellationToken); });
        Assert.Throws<ArgumentException>(() => client.ReferenceTask(".."));
        Assert.Throws<ArgumentException>(() => { _ = new MidjourneyImageClient(factory, MidjourneyFixture.Options(requestLimit: 1)).SubmitAsync(MidjourneyImageOperation.Imagine, MidjourneyFixture.Body(), TestContext.Current.CancellationToken); });
        var body = MidjourneyFixture.Body(); body["future"] = "\ud800"; Assert.Throws<EncoderFallbackException>(() => { _ = client.SubmitAsync(MidjourneyImageOperation.Imagine, body, TestContext.Current.CancellationToken); });
        Assert.Equal(0, factory.Creates);
    }
    /// <summary>批量原始 ids 不能覆盖引用或重复关联，大小写别名也明确失败。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void InvalidBatchInputFailsBeforeHttp(bool alias)
    {
        using var factory = MidjourneyFixture.Factory(); var client = new MidjourneyImageClient(factory, MidjourneyFixture.Options()); var reference = client.ReferenceTask("task-fixture");
        Assert.Throws<ArgumentException>(() => { _ = client.GetManyAsync([reference], new JsonObject { [alias ? "IDS" : "ids"] = new JsonArray("override") }, TestContext.Current.CancellationToken); });
        Assert.Throws<ArgumentException>(() => { _ = client.GetManyAsync([reference, reference], cancellationToken: TestContext.Current.CancellationToken); }); Assert.Equal(0, factory.Creates);
    }
    /// <summary>完整结果的编码、重复成员、实际字节、声明长度和媒体类型都必须有效。</summary>
    [Theory]
    [InlineData("bytes")] [InlineData("length")] [InlineData("utf8")] [InlineData("duplicates")] [InlineData("type")] [InlineData("charset")]
    public async Task CompleteNativeResponseBoundariesAreEnforced(string kind)
    {
        var bytes = Encoding.UTF8.GetBytes(MidjourneyFixture.Part("submitted").GetRawText()); if (kind == "utf8") bytes = [255];
        if (kind == "duplicates") bytes = Encoding.UTF8.GetBytes("{\"code\":1,\"result\":\"id\",\"future\":1,\"future\":2}");
        using var source = new MidjourneyReadStream(bytes); using var factory = MidjourneyFixture.Factory(); factory.OnSend = (_, _) =>
        { var content = new StreamContent(source); content.Headers.ContentType = new(kind == "type" ? "text/event-stream" : "application/json"); if (kind == "charset") content.Headers.ContentType.CharSet = "utf-16"; if (kind == "length") content.Headers.ContentLength = bytes.Length + 1; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }); };
        await Assert.ThrowsAsync<InvalidDataException>(() => new MidjourneyImageClient(factory, MidjourneyFixture.Options(responseLimit: kind == "bytes" ? bytes.Length - 1 : bytes.Length + 100)).SubmitAsync(MidjourneyImageOperation.Imagine, MidjourneyFixture.Body(), TestContext.Current.CancellationToken));
        Assert.True(source.Disposed); Assert.Equal(1, factory.Disposals); Assert.Equal(1, factory.Calls);
    }
    /// <summary>最后的读取、流/客户端释放和取消必须成功，才交付任何确认或任务。</summary>
    [Theory]
    [InlineData("read")] [InlineData("dispose")] [InlineData("client")] [InlineData("cancel")]
    public async Task LateFailuresCannotReleaseAcceptedSubmission(string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var source = new MidjourneyReadStream(Encoding.UTF8.GetBytes(MidjourneyFixture.Part("submitted").GetRawText())) { FailRead = kind == "read", FailDispose = kind == "dispose", OnEof = kind == "cancel" ? cancellation.Cancel : null };
        using var factory = MidjourneyFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) }); if (kind == "client") factory.OnDisposeClient = () => throw new IOException("fixture client disposal");
        var client = new MidjourneyImageClient(factory, MidjourneyFixture.Options());
        if (kind == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SubmitAsync(MidjourneyImageOperation.Imagine, MidjourneyFixture.Body(), cancellation.Token));
        else await Assert.ThrowsAsync<IOException>(() => client.SubmitAsync(MidjourneyImageOperation.Imagine, MidjourneyFixture.Body(), cancellation.Token));
        Assert.True(source.Disposed); Assert.Equal(1, factory.Disposals); Assert.Equal(1, factory.Calls); Assert.False(factory.Disposed);
    }
}
