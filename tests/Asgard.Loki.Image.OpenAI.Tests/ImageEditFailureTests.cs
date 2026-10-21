using System.Text;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>原生失败与传输尾部验证，不能用有效 JSON 前缀或已构建结果掩盖读取/释放失败。</summary>
public sealed class ImageEditFailureTests
{
    /// <summary>HTTP 错误完整保留未知编码字节；2xx 中的业务错误仍失败，不回退生成或第二目标。</summary>
    [Theory]
    [InlineData(false, 400)] [InlineData(true, 400)] [InlineData(false, 429)] [InlineData(true, 429)]
    [InlineData(false, 503)] [InlineData(true, 503)] [InlineData(false, 200)] [InlineData(true, 200)]
    public async Task NativeError_PreservesStatusTypeAndBody(bool multipart, int code)
    {
        var body = code == 200 ? Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"invalid_mask\",\"future\":null},\"future\":false}") : new byte[] { 255, 0, 128, 13, 10 };
        var media = code == 200 ? "application/json" : "application/octet-stream; charset=unknown";
        using var one = new EditReadStream([1]); using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        var stream = new EditReadStream(body);
        using var handler = new StubHttpHandler(async (message, token) =>
        { if (multipart) { using var uploaded = new MemoryStream(); await message.Content!.CopyToAsync(uploaded, token); } return EditFixture.Response(stream, media, body.Length, code); });
        using var factory = new EditTestFactory(handler);
        var adapter = new OpenAiImageEditAdapter(factory, EditFixture.Options);
        await using var client = new LokiClientBuilder().AddImage(new() { EditRoutes = [new() { Model = "pictures", Targets = [adapter, adapter] }] }).Build();
        var failure = await Assert.ThrowsAsync<ImageEditUpstreamException>(() => EditFixture.Send(client, multipart, one, two, mask, TestContext.Current.CancellationToken));
        Assert.Equal(code, failure.StatusCode); Assert.Equal(media, failure.ContentType); Assert.Equal(body, failure.Body.ToArray());
        Assert.True(stream.Eof); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
        Assert.All(new[] { one, two, mask }, upload => Assert.False(upload.Disposed));
    }
    /// <summary>不完整/歧义原生图片结果不能被完整容器外观掩盖；不以猜测字段来制造图片。</summary>
    [Theory]
    [InlineData("[]")] [InlineData("{}")] [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":{}}")] [InlineData("{\"data\":[null]}")] [InlineData("{\"data\":[{}]}")]
    [InlineData("{\"data\":[{\"b64_json\":false}]}")] [InlineData("{\"data\":[{\"url\":1}]}")]
    [InlineData("{\"data\":[{\"b64_json\":\" \"}]}")] [InlineData("{\"data\":[{\"url\":\"https://cdn.invalid\",\"revised_prompt\":false}]}")]
    [InlineData("{\"data\":[{\"url\":\"https://cdn.invalid\",\"future\":{\"x\":1,\"x\":2}}]}")]
    [InlineData("{\"data\":[],\"data\":[{\"url\":\"https://cdn.invalid\"}]}")]
    public async Task InvalidNativeResult_DoesNotReturnSuccess(string raw)
    {
        using var stream = new EditReadStream(Encoding.UTF8.GetBytes(raw)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(EditFixture.Response(stream)));
        using var factory = new EditTestFactory(handler); await using var client = EditFixture.Build(new OpenAiImageEditAdapter(factory, EditFixture.Options));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Image.EditAsync(EditFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>有效 JSON 前缀后读取/长度/流释放/客户端释放/取消任一失败都不返回成功。</summary>
    [Theory]
    [InlineData(false, "read")] [InlineData(true, "read")] [InlineData(false, "short")] [InlineData(true, "short")]
    [InlineData(false, "long")] [InlineData(true, "long")] [InlineData(false, "dispose")] [InlineData(true, "dispose")]
    [InlineData(false, "client-dispose")] [InlineData(true, "client-dispose")] [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    public async Task TailFailure_PreventsSuccess(bool multipart, string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var one = new EditReadStream([1]); using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        var bytes = Encoding.UTF8.GetBytes(EditFixture.Text("edit-json-response"));
        var stream = new EditReadStream(bytes) { FailRead = kind == "read", FailDispose = kind == "dispose", OnEof = kind == "cancel" ? cancellation.Cancel : null };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(EditFixture.Response(stream, length: bytes.Length + (kind == "short" ? 1 : kind == "long" ? -1 : 0))));
        using var factory = new EditTestFactory(handler) { FailClientDispose = kind == "client-dispose" }; await using var client = EditFixture.Build(new OpenAiImageEditAdapter(factory, EditFixture.Options));
        var task = EditFixture.Send(client, multipart, one, two, mask, cancellation.Token);
        if (kind == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else if (kind is "short" or "long") await Assert.ThrowsAsync<InvalidDataException>(() => task);
        else await Assert.ThrowsAsync<IOException>(() => task);
        Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
        Assert.All(new[] { one, two, mask }, upload => Assert.False(upload.Disposed));
    }
    /// <summary>非 JSON 或非原生编码不能转成编辑成功；不读 SSE 到结束再伪装完整结果。</summary>
    [Theory] [InlineData(null)] [InlineData("text/event-stream")] [InlineData("text/plain")] [InlineData("image/png")] [InlineData("application/json; charset=utf-16")]
    public async Task WrongSuccessMedia_FailsBeforeReading(string? media)
    {
        var stream = new EditReadStream(Encoding.UTF8.GetBytes(EditFixture.Text("edit-json-response")));
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(EditFixture.Response(stream, media)));
        using var factory = new EditTestFactory(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new OpenAiImageEditAdapter(factory, EditFixture.Options).EditAsync(EditFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(0, stream.Reads); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>无效 UTF-8 和损坏 JSON 不做替换/前缀恢复。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task MalformedResponse_IsNotRepaired(bool utf8)
    {
        var bytes = utf8 ? new byte[] { 123, 34, 120, 34, 58, 34, 255, 34, 125 } : Encoding.UTF8.GetBytes("{\"data\":[");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(EditFixture.Response(new EditReadStream(bytes))));
        var task = new OpenAiImageEditAdapter(new StubHttpClientFactory(handler), EditFixture.Options).EditAsync(EditFixture.Request(), TestContext.Current.CancellationToken);
        if (utf8) await Assert.ThrowsAsync<DecoderFallbackException>(() => task); else await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => task);
        Assert.Equal(1, handler.Calls);
    }
    /// <summary>上传中途失败后流保持开放，不对其他目标重新发送剩余图片或遮罩。</summary>
    [Fact]
    public async Task UploadFailure_DoesNotRetryOrDisposeBorrowedSources()
    {
        using var one = new EditReadStream([0, 1, 2, 3]) { FailRead = true }; using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        using var handler = new StubHttpHandler(async (message, token) =>
        { using var bytes = new MemoryStream(); await message.Content!.CopyToAsync(bytes, token); return EditFixture.Response(); });
        using var factory = new EditTestFactory(handler); await using var client = EditFixture.Build(new OpenAiImageEditAdapter(factory, EditFixture.Options));
        await Assert.ThrowsAsync<HttpRequestException>(() => EditFixture.Send(client, true, one, two, mask, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls); Assert.True(one.Reads > 1); Assert.Equal(0, two.Reads); Assert.Equal(0, mask.Reads);
        Assert.All(new[] { one, two, mask }, upload => Assert.False(upload.Disposed)); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>原生错误优先于尾部重复 null，原始字节副本不被调用者后续修改。</summary>
    [Fact]
    public async Task ErrorBeforeDuplicateNull_IsStillFailureAndBytesAreCopied()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"native\"},\"error\":null,\"data\":[{\"url\":\"https://cdn.invalid\"}]}");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(EditFixture.Response(new EditReadStream(bytes))));
        var error = await Assert.ThrowsAsync<ImageEditUpstreamException>(() => new OpenAiImageEditAdapter(new StubHttpClientFactory(handler), EditFixture.Options).EditAsync(EditFixture.Request(), TestContext.Current.CancellationToken));
        var expected = bytes.ToArray(); Array.Clear(bytes); Assert.Equal(expected, error.Body.ToArray()); Assert.Equal(200, error.StatusCode);
        var source = new byte[] { 1, 2 }; var copy = new ImageEditUpstreamException("fixture", 400, source, null); source[0] = 9; Assert.Equal(1, copy.Body.Span[0]);
    }
    /// <summary>源正常 EOF 后响应内容本身释放失败，已构造的结果也不能返回。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ResponseDisposalFailure_DoesNotReturnSuccess(bool multipart)
    {
        using var one = new EditReadStream([1]); using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        var stream = new EditReadStream(Encoding.UTF8.GetBytes(EditFixture.Text("edit-json-response")));
        using var handler = new StubHttpHandler((_, _) =>
        {
            var content = new EditDisposalContent(stream); content.Headers.ContentType = new("application/json");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        });
        using var factory = new EditTestFactory(handler); await using var client = EditFixture.Build(new OpenAiImageEditAdapter(factory, EditFixture.Options));
        await Assert.ThrowsAsync<IOException>(() => EditFixture.Send(client, multipart, one, two, mask, TestContext.Current.CancellationToken));
        Assert.True(stream.Eof); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>上传过程中取消后不能继续读取后续图片/遮罩或再向同一/其他目标派发。</summary>
    [Fact]
    public async Task UploadCancellation_DoesNotSendFollowingParts()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var one = new EditReadStream([1]) { OnEof = cancellation.Cancel }; using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        using var handler = new StubHttpHandler(async (message, token) =>
        { using var wire = new MemoryStream(); await message.Content!.CopyToAsync(wire, token); return EditFixture.Response(); });
        using var factory = new EditTestFactory(handler); await using var client = EditFixture.Build(new OpenAiImageEditAdapter(factory, EditFixture.Options));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EditFixture.Send(client, true, one, two, mask, cancellation.Token));
        Assert.Equal(0, two.Reads); Assert.Equal(0, mask.Reads); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
        Assert.All(new[] { one, two, mask }, stream => Assert.False(stream.Disposed));
    }
}
