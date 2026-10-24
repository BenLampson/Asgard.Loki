using System.Net;
using System.Text.Json;
using Asgard.Loki.Image.Volcengine;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Image.Generation.Tests;

/// <summary>统一结果保留原始内容，图片二进制只有显式请求才解码或下载。</summary>
public sealed class ImageContentTests
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>无法由文件签名识别的图片仍保留供应商明确声明的原生 MIME。</summary>
    [Fact]
    public async Task Base64_PreservesDeclaredMimeType()
    {
        using var http = new StubHttpHandler((_, _) => throw new InvalidOperationException("No IO"));
        var image = new LokiGeneratedImage(new GeneratedImage(JsonSerializer.SerializeToElement(new { }), null, "AQID")
        { ContentType = "image/avif" });
        var content = await GenerationFixture.Client(new GenerationHttpFactory(http))
            .GetImageContentAsync(image, TestContext.Current.CancellationToken);
        Assert.Equal("image/avif", content.ContentType); Assert.Equal(new byte[] { 1, 2, 3 }, content.Bytes.ToArray());
        Assert.Equal(0, http.Calls);
    }

    /// <summary>原生媒体类型和 data URI 类型冲突时不能选择其中一个掩盖不一致。</summary>
    [Fact]
    public async Task ConflictingBase64MimeTypes_AreRejected()
    {
        using var http = new StubHttpHandler((_, _) => throw new InvalidOperationException("No IO"));
        var image = new LokiGeneratedImage(new GeneratedImage(JsonSerializer.SerializeToElement(new { }), null, "data:image/jpeg;base64,AQID")
        { ContentType = "image/png" });
        await Assert.ThrowsAsync<InvalidDataException>(async () => await GenerationFixture.Client(new GenerationHttpFactory(http))
            .GetImageContentAsync(image, TestContext.Current.CancellationToken));
    }

    /// <summary>Base64、data URI 和直接二进制均可获取，媒体类型按明确来源或签名识别。</summary>
    [Theory]
    [InlineData("base64")] [InlineData("data-uri")] [InlineData("binary")]
    public async Task LocalContent_DoesNotCreateHttpClient(string kind)
    {
        using var http = new StubHttpHandler((_, _) => throw new InvalidOperationException("No IO"));
        var transport = new GenerationHttpFactory(http);
        var native = new GeneratedImage(JsonSerializer.SerializeToElement(new { }), null,
            kind == "base64" ? Convert.ToBase64String(Png) : kind == "data-uri" ? "data:image/png;base64," + Convert.ToBase64String(Png) : null)
        { Content = kind == "binary" ? new(Png, "image/png") : null };
        var content = await GenerationFixture.Client(transport).GetImageContentAsync(new(native), TestContext.Current.CancellationToken);
        Assert.Equal(Png, content.Bytes.ToArray()); Assert.Equal("image/png", content.ContentType); Assert.Equal(0, transport.Calls);
    }

    /// <summary>生成默认不下载；显式下载只使用独立命名客户端且不转发默认认证和专属头。</summary>
    [Fact]
    public async Task UrlDownload_IsExplicitAndDoesNotForwardCredentials()
    {
        using var http = new StubHttpHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(GenerationFixture.Response("""{"data":[{"url":"https://cdn.example.invalid/a.png?sig=a%2Bb"}]}"""));
            Assert.Equal("https://cdn.example.invalid/a.png?sig=a%2Bb", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("X-Application-Secret"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(Png) });
        });
        var transport = new GenerationHttpFactory(http); var client = GenerationFixture.Client(transport);
        var result = await client.GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(1, http.Calls);
        var image = Assert.Single(result.Images);
        var content = await client.GetImageContentAsync(image, TestContext.Current.CancellationToken);
        Assert.Equal(Png, content.Bytes.ToArray()); Assert.Equal("image/png", content.ContentType);
        Assert.Contains("Asgard.Loki.Image.Content", transport.Names);
        Assert.Equal(2, http.Calls);
    }

    /// <summary>下载失败不重发生成，原始结果和 URL 仍可用于之后单独获取。</summary>
    [Fact]
    public async Task DownloadFailure_DoesNotDiscardOrRegenerateResult()
    {
        using var http = new StubHttpHandler((request, _) => Task.FromResult(request.Method == HttpMethod.Post
            ? GenerationFixture.Response("""{"data":[{"url":"https://cdn.example.invalid/expired.png"}]}""")
            : new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var client = GenerationFixture.Client(new GenerationHttpFactory(http));
        var result = await client.GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<HttpRequestException>(async () => await client.GetImageContentAsync(result.Images[0], TestContext.Current.CancellationToken));
        Assert.Equal(LokiImageGenerationState.Succeeded, result.State);
        Assert.NotNull(result.RawResponse); Assert.Equal("https://cdn.example.invalid/expired.png", result.Images[0].Url);
        Assert.Equal(2, http.Calls);
    }

    /// <summary>字节上限、正文截断及资源释放错误不能被成功图片内容掩盖。</summary>
    [Theory]
    [InlineData("base64-limit")] [InlineData("download-limit")] [InlineData("length")] [InlineData("dispose")]
    public async Task ContentBoundaryFailure_DoesNotReturnSuccess(string kind)
    {
        using var http = new StubHttpHandler((_, _) =>
        {
            var content = kind == "dispose" ? (HttpContent)new StreamContent(new FaultImageStream(Png)) : new ByteArrayContent(Png);
            if (kind == "length") content.Headers.ContentLength = 20;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        var client = new LokiImageFactory(GenerationFixture.Registration(GenerationFixture.Target()), new GenerationHttpFactory(http),
            new() { MaximumBytes = kind.EndsWith("limit", StringComparison.Ordinal) ? 4 : 32 }).CreateClient();
        var image = new LokiGeneratedImage(new GeneratedImage(JsonSerializer.SerializeToElement(new { }), "https://cdn.example.invalid/a.png",
            kind == "base64-limit" ? Convert.ToBase64String(Png) : null));
        if (kind == "dispose")
            await Assert.ThrowsAsync<IOException>(async () => await client.GetImageContentAsync(image, TestContext.Current.CancellationToken));
        else
            await Assert.ThrowsAsync<InvalidDataException>(async () => await client.GetImageContentAsync(image, TestContext.Current.CancellationToken));
        Assert.Equal(kind == "base64-limit" ? 0 : 1, http.Calls);
    }

    /// <summary>无效载荷和非 HTTP 地址在任何下载之前失败。</summary>
    [Theory]
    [InlineData("invalid-base64")] [InlineData("file-url")] [InlineData("missing")]
    public async Task InvalidImageCarrier_FailsWithoutNetwork(string kind)
    {
        using var http = new StubHttpHandler((_, _) => throw new InvalidOperationException("No IO"));
        var image = new LokiGeneratedImage(new GeneratedImage(JsonSerializer.SerializeToElement(new { }),
            kind == "file-url" ? "file:///tmp/a.png" : null, kind == "invalid-base64" ? "not-base64" : null));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await GenerationFixture.Client(new GenerationHttpFactory(http))
            .GetImageContentAsync(image, TestContext.Current.CancellationToken));
        Assert.Equal(0, http.Calls);
    }

    /// <summary>同次生成的成功和失败图片都保留，失败条目不能被下载为成功图片。</summary>
    [Fact]
    public async Task PartialImageFailure_IsPreserved()
    {
        const string body = """{"data":[{"b64_json":"iVBORw0KGgo="},{"error":{"code":"fixture-filtered","message":"blocked"}}],"usage":{"count":1}}""";
        using var http = new StubHttpHandler((_, _) => Task.FromResult(GenerationFixture.Response(body)));
        var client = GenerationFixture.Client(new GenerationHttpFactory(http), GenerationFixture.Target(new VolcengineImageGenerationHandler()));
        var result = await client.GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(LokiImageGenerationState.PartiallySucceeded, result.State); Assert.Equal(2, result.Images.Count);
        Assert.NotNull(result.Images[1].Error); Assert.Equal(body, System.Text.Encoding.UTF8.GetString(result.RawResponse!.Body.Span));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.GetImageContentAsync(result.Images[1], TestContext.Current.CancellationToken));
        Assert.Equal(1, http.Calls);
    }

    /// <summary>合法空图片响应不被标记为成功生成。</summary>
    [Fact]
    public async Task EmptyImages_AreNotSuccessfulGeneration()
    {
        using var http = new StubHttpHandler((_, _) => Task.FromResult(GenerationFixture.Response("""{"data":[]}""")));
        var result = await GenerationFixture.Client(new GenerationHttpFactory(http), GenerationFixture.Target(new VolcengineImageGenerationHandler()))
            .GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(LokiImageGenerationState.NoImage, result.State); Assert.Empty(result.Images);
    }
}
