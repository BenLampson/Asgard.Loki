using System.Text;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>生成结果/完成项必须等待真实传输尾部，不能因已有有效 JSON 或图片而先返回成功。</summary>
public sealed class ImageGenerationLifecycleTests
{
    /// <summary>非流式和 SSE 的长度/读取/释放/取消失败都覆盖已构造结果，不发布成功。</summary>
    [Theory]
    [InlineData(false, "read-tail")] [InlineData(true, "read-tail")] [InlineData(false, "short")] [InlineData(true, "short")]
    [InlineData(false, "long")] [InlineData(true, "long")] [InlineData(false, "stream-dispose")] [InlineData(true, "stream-dispose")]
    [InlineData(false, "response-dispose")] [InlineData(true, "response-dispose")] [InlineData(false, "client-dispose")] [InlineData(true, "client-dispose")]
    [InlineData(false, "cancel-tail")] [InlineData(true, "cancel-tail")]
    public async Task TailFailure_DoesNotPublishSuccess(bool streaming, string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var bytes = Encoding.UTF8.GetBytes(streaming ? GenerationFixture.StreamBody : EditFixture.Text("generation-response"));
        var stream = new EditReadStream(bytes) { FailAfterBytes = kind == "read-tail" ? bytes.Length : null, FailDispose = kind == "stream-dispose", OnEof = kind == "cancel-tail" ? cancellation.Cancel : null };
        using var handler = new StubHttpHandler((_, _) =>
        {
            var media = streaming ? "text/event-stream" : "application/json";
            if (kind == "response-dispose")
            {
                var content = new EditDisposalContent(stream); content.Headers.ContentType = new(media); return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
            }
            return Task.FromResult(EditFixture.Response(stream, media, bytes.Length + (kind == "short" ? 1 : kind == "long" ? -1 : 0)));
        });
        using var factory = new EditTestFactory(handler) { FailClientDispose = kind == "client-dispose" };
        await using var client = GenerationFixture.Build(new OpenAiImageAdapter(factory, GenerationFixture.Options)); List<ImageGenerationEvent> seen = [];
        var task = Run(client.Image, streaming, seen, cancellation.Token);
        if (kind == "cancel-tail") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else if (kind is "short" or "long") await Assert.ThrowsAsync<InvalidDataException>(() => task);
        else await Assert.ThrowsAsync<IOException>(() => task);
        Assert.DoesNotContain(seen, item => item.Type == "image_generation.completed"); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
        Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生失败不是普通进度终态；保留旧异常类型、原文和 SSE 身份，即使错误晚于完成项。</summary>
    [Theory] [InlineData("type")] [InlineData("event")] [InlineData("failed")] [InlineData("root")] [InlineData("duplicate-null")]
    public async Task NativeErrorAfterHeldCompletion_PreservesIdentity(string kind)
    {
        var raw = kind switch
        {
            "type" => "{\"type\":\"error\",\"code\":\"native_error\",\"future\":{\"value\":null}}",
            "failed" => "{\"type\":\"image_generation.failed\",\"future\":{\"text\":\"😀\",\"value\":null}}",
            "root" => "{\"type\":\"vendor.error\",\"error\":{\"code\":\"native_error\"},\"future\":false}",
            "duplicate-null" => "{\"type\":\"vendor.error\",\"error\":{\"code\":\"native_error\"},\"error\":null}",
            _ => "{\"type\":\"vendor.error\",\"message\":\"native\",\"future\":false}"
        };
        var name = kind == "event" ? "error" : kind == "failed" ? "image_generation.failed" : kind == "type" ? "message" : "vendor.error";
        var body = GenerationFixture.Partial + GenerationFixture.Completed + "event: " + name + "\nid: generation-error\nretry: 50\ndata: " + raw + "\n\n";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(GenerationFixture.Response(true, body))); using var factory = new EditTestFactory(handler);
        await using var client = GenerationFixture.Build(new OpenAiImageAdapter(factory, GenerationFixture.Options)); List<ImageGenerationEvent> seen = [];
        var error = await Assert.ThrowsAsync<ImageUpstreamException>(async () =>
        { await foreach (var item in client.Image.StreamAsync(GenerationFixture.Request(true), TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.DoesNotContain(seen, item => item.Type == "image_generation.completed"); Assert.Equal(200, error.StatusCode); Assert.Equal(raw, error.ResponseBody);
        Assert.Equal(Encoding.UTF8.GetBytes(raw), error.Body.ToArray()); Assert.Equal("text/event-stream; charset=utf-8", error.ContentType); Assert.NotNull(error.GenerationEvent);
        Assert.Equal(raw, error.GenerationEvent.Data.GetRawText()); Assert.Equal(name, error.GenerationEvent.EventName); Assert.Equal("generation-error", error.GenerationEvent.EventId);
        Assert.Equal(50, error.GenerationEvent.RetryMilliseconds); Assert.Equal(new("pictures", "native-generation-model", "generation-target"), error.GenerationEvent.Metadata);
        Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>HTTP/2xx 原生业务错误保留真实状态和任意原始字节，不派发第二目标。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task UpstreamErrors_KeepRawBytesAndDoNotFailOver(bool streaming, bool businessError)
    {
        var bytes = businessError ? Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"native_error\"},\"future\":null}") : new byte[] { 255, 128, 0, 13, 10 };
        var media = businessError ? "application/json" : "application/octet-stream; charset=unknown"; var status = businessError ? 200 : 429;
        using var firstHandler = new StubHttpHandler((_, _) => Task.FromResult(EditFixture.Response(new EditReadStream(bytes), media, bytes.Length, status)));
        using var secondHandler = new StubHttpHandler((_, _) => throw new InvalidOperationException("no failover"));
        using var factory = new EditTestFactory(firstHandler); var first = new OpenAiImageAdapter(factory, GenerationFixture.Options); var second = new OpenAiImageAdapter(new StubHttpClientFactory(secondHandler), GenerationFixture.Options);
        await using var client = GenerationFixture.Build(first, second);
        var error = await Assert.ThrowsAsync<ImageUpstreamException>(() => Run(client.Image, streaming, [], TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal(media, error.ContentType); Assert.Null(error.GenerationEvent);
        if (businessError) Assert.Equal(Encoding.UTF8.GetString(bytes), error.ResponseBody); else Assert.Throws<DecoderFallbackException>(() => error.ResponseBody);
        Assert.Equal(1, firstHandler.Calls); Assert.Equal(0, secondHandler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>普通 JSON 成功不能被伪造成 SSE，SSE/text/错误 charset 也不能替代完整 JSON。</summary>
    [Theory] [InlineData(false, "text/event-stream")] [InlineData(false, "text/plain")] [InlineData(false, "application/json; charset=utf-16")]
    [InlineData(true, "application/json")] [InlineData(true, "text/plain")] [InlineData(true, "text/event-stream; charset=utf-16")]
    public async Task WrongSuccessfulMedia_IsNotConverted(bool streaming, string media)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(EditFixture.Response(new EditReadStream(Encoding.UTF8.GetBytes(EditFixture.Text("generation-response"))), media)));
        using var factory = new EditTestFactory(handler); await using var client = GenerationFixture.Build(new OpenAiImageAdapter(factory, GenerationFixture.Options));
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(client.Image, streaming, [], TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>UTF-8 尾部错误不能被替换字符或先前暂存的终态掩盖。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InvalidUtf8_IsNotRepaired(bool streaming)
    {
        var bytes = Encoding.UTF8.GetBytes(streaming ? GenerationFixture.Completed : EditFixture.Text("generation-response")).Concat(new byte[] { 255 }).ToArray();
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(EditFixture.Response(new EditReadStream(bytes), streaming ? "text/event-stream" : "application/json")));
        await using var client = GenerationFixture.Build(new OpenAiImageAdapter(new StubHttpClientFactory(handler), GenerationFixture.Options)); List<ImageGenerationEvent> seen = [];
        await Assert.ThrowsAsync<DecoderFallbackException>(() => Run(client.Image, streaming, seen, TestContext.Current.CancellationToken)); Assert.Empty(seen);
    }
    /// <summary>提前停止只释放此次响应/客户端；枚举 token 的取消也能抑制 EOF 之后的完成项。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task EarlyStopOrEnumeratorCancellation_CleansTransport(bool cancel, bool sdk)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stream = new EditReadStream(Encoding.UTF8.GetBytes(GenerationFixture.StreamBody)) { FragmentSize = 1, OnEof = cancel ? cancellation.Cancel : null };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(EditFixture.Response(stream, "text/event-stream"))); using var factory = new EditTestFactory(handler);
        var adapter = new OpenAiImageAdapter(factory, GenerationFixture.Options); await using var client = GenerationFixture.Build(adapter);
        var source = sdk ? client.Image.StreamAsync(GenerationFixture.Request(true), CancellationToken.None) : adapter.StreamAsync(GenerationFixture.Request(true), CancellationToken.None);
        await using (var iterator = source.GetAsyncEnumerator(cancellation.Token))
        {
            if (!cancel) { Assert.True(await iterator.MoveNextAsync()); Assert.Equal("image_generation.partial_image", iterator.Current.Type); }
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            { while (await iterator.MoveNextAsync()) Assert.NotEqual("image_generation.completed", iterator.Current.Type); });
        }
        Assert.Equal(cancel, stream.Eof); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>无效 JSON 容器/重复字段不能因有对象外观而成为完整生成成功。</summary>
    [Theory] [InlineData("[]")] [InlineData("{}")] [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"url\":1}]}")] [InlineData("{\"data\":[{}]}")]
    [InlineData("{\"data\":[{\"url\":\"https://cdn.invalid\",\"future\":{\"x\":1,\"x\":2}}]}")]
    public async Task InvalidJsonContainer_DoesNotReturnSuccess(string raw)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(GenerationFixture.Response(false, raw)));
        await using var client = GenerationFixture.Build(new OpenAiImageAdapter(new StubHttpClientFactory(handler), GenerationFixture.Options));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Image.GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>有名但无效的长度在非流式/流式读取前失败，不能视为未声明而跳过校验。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InvalidDeclaredLength_FailsBeforeRead(bool streaming)
    {
        var stream = new EditReadStream(Encoding.UTF8.GetBytes(streaming ? GenerationFixture.StreamBody : EditFixture.Text("generation-response")));
        using var handler = new StubHttpHandler((_, _) =>
        {
            var response = EditFixture.Response(stream, streaming ? "text/event-stream" : "application/json");
            Assert.True(response.Content.Headers.TryAddWithoutValidation("Content-Length", "not-an-integer")); return Task.FromResult(response);
        });
        using var factory = new EditTestFactory(handler); await using var client = GenerationFixture.Build(new OpenAiImageAdapter(factory, GenerationFixture.Options));
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(client.Image, streaming, [], TestContext.Current.CancellationToken)); Assert.Equal(0, stream.Reads);
        Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>旧异常签名仍返回原文，新字节构造不别名引用输入，也不伪造错误事件。</summary>
    [Fact]
    public void ExceptionCompatibility_PreservesOldSurfaceAndCopiesRawBody()
    {
        var legacy = new ImageUpstreamException(message: "legacy", statusCode: 400, responseBody: "原文 😀");
        Assert.Equal(400, legacy.StatusCode); Assert.Equal("原文 😀", legacy.ResponseBody); Assert.Equal(Encoding.UTF8.GetBytes("原文 😀"), legacy.Body.ToArray());
        Assert.Null(legacy.ContentType); Assert.Null(legacy.GenerationEvent);
        var source = new byte[] { 255, 0, 128 }; var raw = new ImageUpstreamException("native", 403, source, "application/octet-stream"); source[0] = 9;
        Assert.Equal(new byte[] { 255, 0, 128 }, raw.Body.ToArray()); Assert.Throws<DecoderFallbackException>(() => raw.ResponseBody);
    }
    /// <summary>两种公开方法都实际消费到末尾，并保存已公开事件以检查终态是否被泄露。</summary>
    private static async Task Run(ImageClient client, bool streaming, List<ImageGenerationEvent> seen, CancellationToken token)
    {
        if (!streaming) { await client.GenerateAsync(GenerationFixture.Request(), token); return; }
        await foreach (var item in client.StreamAsync(GenerationFixture.Request(true), token)) seen.Add(item);
    }
}
