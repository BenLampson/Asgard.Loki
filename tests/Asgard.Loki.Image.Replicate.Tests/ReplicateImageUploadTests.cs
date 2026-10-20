using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Image.Replicate.Tests;

/// <summary>显式单次图像模型输入上传，不扩展独立文件管理或隐式预测流程。</summary>
public sealed class ReplicateImageUploadTests
{
    /// <summary>原生 content 字段、文件名、媒体类型和借用流剩余字节保持，确认原文完整返回。</summary>
    [Fact]
    public async Task ExplicitInputUploadKeepsBytesOwnershipAndNativeMetadata()
    {
        using var source = new ReplicateReadStream([9, 1, 2, 3]);
        Assert.Equal(1, await source.ReadAsync(new byte[1], TestContext.Current.CancellationToken));
        using var factory = ReplicateFixture.Factory(); factory.OnSend = async (message, token) =>
        {
            Assert.Equal("https://replicate-images.example.invalid/prefix/v1/files", message.RequestUri!.AbsoluteUri); Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("fixture-replicate-key", message.Headers.Authorization!.Parameter); Assert.False(message.Headers.Contains("Prefer"));
            using var bytes = new MemoryStream(); await message.Content!.CopyToAsync(bytes, token); var raw = bytes.ToArray(); var text = Encoding.Latin1.GetString(raw);
            Assert.Contains("multipart/form-data", message.Content.Headers.ContentType!.ToString()); Assert.Contains("name=content", text); Assert.Contains("filename=original.png", text); Assert.Contains("Content-Type: image/png", text);
            var start = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4; Assert.Equal(new byte[] { 1, 2, 3 }, raw.AsSpan(start, 3).ToArray()); Assert.Equal('\r', text[start + 3]);
            return ReplicateFixture.Json(ReplicateFixture.Part("upload"), HttpStatusCode.Created);
        };
        var adapter = new ReplicateImageAdapter(factory, ReplicateFixture.Options()); var input = await adapter.UploadImageInputAsync(new() { Content = source, FileName = "original.png", ContentType = "image/png" }, TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(ReplicateFixture.Part("upload"), input.Data)); Assert.Equal("https://input.example.invalid/uploaded%2Fimage.png?sig=A%2BB", input.Url);
        Assert.Equal("9007199254740993", input.Data.GetProperty("future").GetProperty("exact").GetRawText()); Assert.False(source.Disposed); Assert.True(source.Eof); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>上传失败不改成 Base64、重传、预测或删除操作。</summary>
    [Theory]
    [InlineData(400)] [InlineData(503)]
    public async Task UploadFailureNeverFallsBackOrStartsPrediction(int status)
    {
        using var source = new ReplicateReadStream([1, 2, 3]); using var factory = ReplicateFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(ReplicateFixture.Json(ReplicateFixture.Part("http_error"), (HttpStatusCode)status));
        var error = await Assert.ThrowsAsync<ImageUpstreamException>(() => new ReplicateImageAdapter(factory, ReplicateFixture.Options()).UploadImageInputAsync(new() { Content = source, FileName = "original.png" }, TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.True(JsonElement.DeepEquals(ReplicateFixture.Part("http_error"), ReplicateFixture.Element(error.ResponseBody))); Assert.False(source.Disposed); Assert.Equal(1, factory.Calls);
    }
    /// <summary>原生上传确认无有效 URL 时不能凭空生成模型输入引用。</summary>
    [Fact]
    public async Task MalformedUploadCannotFabricateInputUrl()
    {
        using var source = new ReplicateReadStream([1]); using var factory = ReplicateFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(ReplicateFixture.Json(ReplicateFixture.Element("{\"id\":\"native-id\",\"urls\":{\"get\":\"file:///secret\"}}")));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ReplicateImageAdapter(factory, ReplicateFixture.Options()).UploadImageInputAsync(new() { Content = source, FileName = "original.png" }, TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls); Assert.False(source.Disposed);
    }
    /// <summary>上传成功 HTTP 中的 API 错误也保留完整正文，不误报缺 URL 或启动后续预测。</summary>
    [Fact]
    public async Task NativeUploadApiErrorRetainsOriginalEnvelope()
    {
        using var source = new ReplicateReadStream([1]); using var factory = ReplicateFixture.Factory("http_error");
        var error = await Assert.ThrowsAsync<ImageUpstreamException>(() => new ReplicateImageAdapter(factory, ReplicateFixture.Options()).UploadImageInputAsync(new() { Content = source, FileName = "original.png" }, TestContext.Current.CancellationToken));
        Assert.Equal(200, error.StatusCode); Assert.True(JsonElement.DeepEquals(ReplicateFixture.Part("http_error"), ReplicateFixture.Element(error.ResponseBody)));
        Assert.False(source.Disposed); Assert.Equal(1, factory.Calls);
    }
    /// <summary>单次上传字节上限在读取中生效，不检查源长度、不回卷、不关闭源流。</summary>
    [Fact]
    public async Task UploadCapRejectsPayloadWithoutReplayOrTakingOwnership()
    {
        using var source = new ReplicateReadStream([1, 2, 3]); using var factory = ReplicateFixture.Factory(); factory.OnSend = async (message, token) =>
        { using var target = new MemoryStream(); await message.Content!.CopyToAsync(target, token); return ReplicateFixture.Json(ReplicateFixture.Part("upload")); };
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => new ReplicateImageAdapter(factory, ReplicateFixture.Options(uploadLimit: 2)).UploadImageInputAsync(new() { Content = source, FileName = "original.png" }, TestContext.Current.CancellationToken));
        Assert.IsType<InvalidDataException>(failure.InnerException ?? failure); Assert.False(source.Disposed); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>不支持的公共 multipart/SSE 在读取源和创建工厂之前失败。</summary>
    [Fact]
    public void UnsupportedConveniencesCannotReadInput()
    {
        using var source = new ReplicateReadStream([1, 2, 3]); using var factory = ReplicateFixture.Factory(); var adapter = new ReplicateImageAdapter(factory, ReplicateFixture.Options());
        Assert.Throws<NotSupportedException>(() => adapter.StreamAsync(ReplicateFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Throws<NotSupportedException>(() => { _ = adapter.EditMultipartAsync(new() { Model = "pictures", Prompt = "original", Images = [new() { Content = source, FileName = "original.png" }] }, TestContext.Current.CancellationToken); });
        Assert.Equal(0, factory.Creates); Assert.False(source.Eof); Assert.False(source.Disposed);
    }
}
