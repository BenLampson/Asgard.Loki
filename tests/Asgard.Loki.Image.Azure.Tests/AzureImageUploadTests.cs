using System.Text;
using Xunit;

namespace Asgard.Loki.Image.Azure.Tests;

/// <summary>图片/遮罩借用流不检查长度、不重放、不回卷且不转移释放权。</summary>
public sealed class AzureImageUploadTests
{
    /// <summary>流从当前偏移读取，SDK 不自行打开路径或发送前缀。</summary>
    [Fact]
    public async Task Upload_StartsAtCallerPosition()
    {
        using var stream = new MemoryStream([0xAA, 0xBB, 1, 2, 3]); stream.Position = 2;
        using var factory = new AzureImageTestFactory { OnSend = async (message, token) =>
        {
            var raw = Encoding.Latin1.GetString(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.Contains("\r\n\r\n" + Encoding.Latin1.GetString(new byte[] { 1, 2, 3 }) + "\r\n", raw);
            Assert.DoesNotContain(Encoding.Latin1.GetString(new byte[] { 0xAA, 0xBB }), raw);
            return AzureImageFixture.Json(AzureImageFixture.Part("result"));
        } };
        await Send(factory, stream); Assert.Equal(5, stream.Position); Assert.True(stream.CanRead);
    }

    /// <summary>即使外部处理器试图重放，也不能再次读取借用图片。</summary>
    [Fact]
    public async Task RepeatedSerialization_IsRejectedWithoutClosingSource()
    {
        using var upload = new AzureImageReadStream([1, 2, 255]);
        using var factory = new AzureImageTestFactory { OnSend = async (message, token) =>
        {
            using var first = new MemoryStream(); await message.Content!.CopyToAsync(first, token);
            using var second = new MemoryStream(); await message.Content.CopyToAsync(second, token);
            return AzureImageFixture.Json(AzureImageFixture.Part("result"));
        } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Send(factory, upload)); Assert.Equal(3, upload.Consumed); Assert.False(upload.Disposed); Assert.Equal(1, factory.Calls);
    }

    /// <summary>上传失败或显式上限错误不回卷、关闭输入，也不另发一次请求。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UploadFailure_DoesNotReplayOrOwnStream(bool limit)
    {
        using var upload = new AzureImageReadStream([1, 2, 3, 4, 5, 255]) { FailAfter = limit ? null : 3 };
        using var factory = new AzureImageTestFactory { OnSend = async (message, token) =>
        {
            using var wire = new MemoryStream(); await message.Content!.CopyToAsync(wire, token);
            return AzureImageFixture.Json(AzureImageFixture.Part("result"));
        } };
        if (limit) await Assert.ThrowsAsync<InvalidDataException>(() => Send(factory, upload, 2));
        else
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => Send(factory, upload));
            Assert.IsType<IOException>(error.InnerException);
        }
        Assert.False(upload.Disposed); Assert.True(upload.Consumed > 0); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.ClientDisposals);
    }

    /// <summary>空泛文件名不作为路径访问，未知文件媒体类型不按扩展名推断。</summary>
    [Fact]
    public async Task MissingContentType_IsNotInferredFromFilename()
    {
        using var upload = new AzureImageReadStream([1]);
        using var factory = new AzureImageTestFactory { OnSend = async (message, token) =>
        {
            var raw = Encoding.Latin1.GetString(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.DoesNotContain("image/png", raw); Assert.Contains("not-opened.png", raw); return AzureImageFixture.Json(AzureImageFixture.Part("result"));
        } };
        await Send(factory, upload); Assert.False(upload.Disposed);
    }

    private static Task<ImageGenerationResult> Send(IHttpClientFactory factory, Stream upload, long maxBytes = 10000)
        => new AzureImageEditAdapter(factory, AzureImageFixture.Options(uploadLimit: maxBytes)).EditMultipartAsync(new()
        { Model = "images", Prompt = "original", Images = [new() { Content = upload, FileName = "not-opened.png" }] }, TestContext.Current.CancellationToken);
}
