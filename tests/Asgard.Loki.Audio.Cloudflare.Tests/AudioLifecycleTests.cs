using System.Net;
using System.Text;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.Cloudflare.Tests;

/// <summary>上传、响应及资源最终边界的失败均不得交付成功结果。</summary>
public sealed class AudioLifecycleTests
{
    /// <summary>取消及读取/释放错误不重试，不关闭借用流或工厂。</summary>
    [Theory]
    [InlineData("upload-eof", false)] [InlineData("upload-eof", true)]
    [InlineData("upload-cancel", false)] [InlineData("upload-cancel", true)]
    [InlineData("response-dispose", false)] [InlineData("response-dispose", true)]
    [InlineData("client-dispose", false)] [InlineData("client-dispose", true)]
    [InlineData("cancel-dispose", false)] [InlineData("cancel-dispose", true)]
    public async Task LateFailures(string kind, bool json)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var upload = new AudioUploadProbe(AudioFixture.Bytes) { OnEof = () =>
            { if (kind == "upload-eof") throw new IOException("upload EOF"); if (kind == "upload-cancel") cancellation.Cancel(); } };
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var body = new MemoryStream(); await message.Content!.CopyToAsync(body, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new AudioResponseContent(
                Encoding.UTF8.GetBytes(AudioFixture.Root.GetProperty("responses").GetProperty("raw").GetRawText()), () =>
                { if (kind == "response-dispose") throw new IOException("response dispose"); }) { Headers = { ContentType = new("application/json") } } };
        });
        var factory = new AudioTestFactory(handler) { OnClientDispose = () =>
            { if (kind == "client-dispose") throw new IOException("client dispose"); if (kind == "cancel-dispose") cancellation.Cancel(); } };
        await Assert.ThrowsAnyAsync<Exception>(() => new CloudflareAudioFileAdapter(factory, AudioFixture.Options(json ? CloudflareAudioInputFormat.JsonBase64 : CloudflareAudioInputFormat.RawBinary))
            .TranscribeAsync(AudioFixture.Request(upload), cancellation.Token));
        Assert.False(upload.Disposed); Assert.Equal(1, handler.Calls); Assert.Equal(1, factory.ClientDisposals); Assert.Equal(0, factory.Disposals);
    }
}
